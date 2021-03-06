﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Text;
using dnSpy.Debugger.Evaluation;

namespace dnSpy.Debugger.Breakpoints.Code.CondChecker {
	[Export(typeof(IDbgManagerStartListener))]
	sealed class ClearParsedMessages : IDbgManagerStartListener {
		readonly TracepointMessageCreatorImpl tracepointMessageCreatorImpl;
		[ImportingConstructor]
		ClearParsedMessages(TracepointMessageCreatorImpl tracepointMessageCreatorImpl) => this.tracepointMessageCreatorImpl = tracepointMessageCreatorImpl;
		void IDbgManagerStartListener.OnStart(DbgManager dbgManager) => dbgManager.IsDebuggingChanged += DbgManager_IsDebuggingChanged;
		void DbgManager_IsDebuggingChanged(object sender, EventArgs e) {
			var dbgManager = (DbgManager)sender;
			tracepointMessageCreatorImpl.OnIsDebuggingChanged(dbgManager.IsDebugging);
		}
	}

	sealed class StringBuilderTextColorWriter : ITextColorWriter {
		StringBuilder sb;
		public void SetStringBuilder(StringBuilder sb) => this.sb = sb;
		public void Write(object color, string text) => sb.Append(text);
		public void Write(TextColor color, string text) => sb.Append(text);
	}

	abstract class TracepointMessageCreator {
		public abstract string Create(DbgBoundCodeBreakpoint boundBreakpoint, DbgThread thread, DbgCodeBreakpointTrace trace);
		public abstract void Write(ITextColorWriter output, DbgCodeBreakpointTrace trace);
	}

	[Export(typeof(TracepointMessageCreator))]
	[Export(typeof(TracepointMessageCreatorImpl))]
	sealed class TracepointMessageCreatorImpl : TracepointMessageCreator {
		readonly object lockObj;
		readonly DbgLanguageService dbgLanguageService;
		readonly DebuggerSettings debuggerSettings;
		readonly DbgEvalFormatterSettings dbgEvalFormatterSettings;
		readonly TracepointMessageParser tracepointMessageParser;
		readonly StringBuilderTextColorWriter stringBuilderTextColorWriter;
		Dictionary<string, ParsedTracepointMessage> toParsedMessage;
		WeakReference toParsedMessageWeakRef;
		StringBuilder output;

		DbgBoundCodeBreakpoint boundBreakpoint;
		DbgThread thread;
		DbgStackWalker stackWalker;
		DbgStackFrame[] stackFrames;

		[ImportingConstructor]
		TracepointMessageCreatorImpl(DbgLanguageService dbgLanguageService, DebuggerSettings debuggerSettings, DbgEvalFormatterSettings dbgEvalFormatterSettings) {
			lockObj = new object();
			output = new StringBuilder();
			this.dbgLanguageService = dbgLanguageService;
			this.debuggerSettings = debuggerSettings;
			this.dbgEvalFormatterSettings = dbgEvalFormatterSettings;
			tracepointMessageParser = new TracepointMessageParser();
			stringBuilderTextColorWriter = new StringBuilderTextColorWriter();
			stringBuilderTextColorWriter.SetStringBuilder(output);
			toParsedMessage = CreateCachedParsedMessageDict();
		}

		static Dictionary<string, ParsedTracepointMessage> CreateCachedParsedMessageDict() => new Dictionary<string, ParsedTracepointMessage>(StringComparer.Ordinal);

		internal void OnIsDebuggingChanged(bool isDebugging) {
			lock (lockObj) {
				// Keep the parsed messages if possible (eg. user presses Restart button)
				if (isDebugging) {
					toParsedMessage = toParsedMessageWeakRef?.Target as Dictionary<string, ParsedTracepointMessage> ?? toParsedMessage ?? CreateCachedParsedMessageDict();
					toParsedMessageWeakRef = null;
				}
				else {
					toParsedMessageWeakRef = new WeakReference(toParsedMessage);
					toParsedMessage = CreateCachedParsedMessageDict();
				}
			}
		}

		public override string Create(DbgBoundCodeBreakpoint boundBreakpoint, DbgThread thread, DbgCodeBreakpointTrace trace) {
			if (boundBreakpoint == null)
				throw new ArgumentNullException(nameof(boundBreakpoint));
			var text = trace.Message;
			if (text == null)
				return string.Empty;
			try {
				output.Clear();
				this.boundBreakpoint = boundBreakpoint;
				this.thread = thread;
				var parsed = GetOrCreate(text);
				int maxFrames = parsed.MaxFrames;
				if (parsed.Evaluates && maxFrames < 1)
					maxFrames = 1;
				if (maxFrames > 0 && thread != null) {
					stackWalker = thread.CreateStackWalker();
					stackFrames = stackWalker.GetNextStackFrames(maxFrames);
				}
				Write(parsed, text);
				return output.ToString();
			}
			finally {
				this.boundBreakpoint = null;
				this.thread = null;
				if (stackWalker != null) {
					stackWalker.Close();
					boundBreakpoint.Process.DbgManager.Close(stackFrames);
					stackWalker = null;
					stackFrames = null;
				}
				if (output.Capacity >= 1024) {
					output = new StringBuilder();
					stringBuilderTextColorWriter.SetStringBuilder(output);
				}
			}
		}

		ParsedTracepointMessage GetOrCreate(string text) {
			lock (lockObj) {
				if (toParsedMessage.TryGetValue(text, out var parsed))
					return parsed;
				parsed = tracepointMessageParser.Parse(text);
				toParsedMessage.Add(text, parsed);
				return parsed;
			}
		}

		DbgStackFrame TryGetFrame(int i) {
			var frames = stackFrames;
			if (frames == null || (uint)i >= (uint)frames.Length)
				return null;
			return frames[i];
		}

		void Write(ParsedTracepointMessage parsed, string tracepointMessage) {
			DbgStackFrame frame;
			foreach (var part in parsed.Parts) {
				switch (part.Kind) {
				case TracepointMessageKind.WriteText:
					Write(part.String);
					break;

				case TracepointMessageKind.WriteEvaluatedExpression:
					frame = TryGetFrame(0);
					if (frame == null)
						WriteError();
					else {
						var language = dbgLanguageService.GetCurrentLanguage(thread.Runtime.RuntimeKindGuid);
						var cancellationToken = CancellationToken.None;
						var state = GetTracepointEvalState(boundBreakpoint, language, frame, tracepointMessage, cancellationToken);
						var eeState = state.GetExpressionEvaluatorState(part.String);
						var evalRes = language.ExpressionEvaluator.Evaluate(state.Context, frame, part.String, DbgEvaluationOptions.Expression, eeState, cancellationToken);
						Write(state.Context, frame, language, evalRes, cancellationToken);
					}
					break;

				case TracepointMessageKind.WriteAddress:
					frame = TryGetFrame(part.Number);
					if (frame != null) {
						const DbgStackFrameFormatOptions options =
							DbgStackFrameFormatOptions.ShowParameterTypes |
							DbgStackFrameFormatOptions.ShowFunctionOffset |
							DbgStackFrameFormatOptions.ShowDeclaringTypes |
							DbgStackFrameFormatOptions.ShowNamespaces |
							DbgStackFrameFormatOptions.ShowIntrinsicTypeKeywords;
						frame.Format(stringBuilderTextColorWriter, options);
					}
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteAppDomainId:
					if (thread?.AppDomain?.Id is int adid)
						Write(adid.ToString());
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteBreakpointAddress:
					Write("0x");
					Write(boundBreakpoint.Address.ToString("X"));
					break;

				case TracepointMessageKind.WriteCaller:
					frame = TryGetFrame(part.Number);
					if (frame != null) {
						const DbgStackFrameFormatOptions options =
							DbgStackFrameFormatOptions.ShowDeclaringTypes |
							DbgStackFrameFormatOptions.ShowNamespaces |
							DbgStackFrameFormatOptions.ShowIntrinsicTypeKeywords;
						frame.Format(stringBuilderTextColorWriter, options);
					}
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteCallerModule:
					var module = TryGetFrame(part.Number)?.Module;
					if (module != null)
						Write(module.Filename);
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteCallerOffset:
					frame = TryGetFrame(part.Number);
					if (frame != null) {
						Write("0x");
						Write(frame.FunctionOffset.ToString("X8"));
					}
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteCallerToken:
					frame = TryGetFrame(part.Number);
					if (frame != null && frame.HasFunctionToken) {
						Write("0x");
						Write(frame.FunctionToken.ToString("X8"));
					}
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteCallStack:
					int maxFrames = part.Number;
					for (int i = 0; i < maxFrames; i++) {
						frame = TryGetFrame(i);
						if (frame == null)
							break;
						Write("\t");
						const DbgStackFrameFormatOptions options =
							DbgStackFrameFormatOptions.ShowDeclaringTypes |
							DbgStackFrameFormatOptions.ShowNamespaces |
							DbgStackFrameFormatOptions.ShowIntrinsicTypeKeywords;
						frame.Format(stringBuilderTextColorWriter, options);
						Write(Environment.NewLine);
					}
					Write("\t");
					break;

				case TracepointMessageKind.WriteFunction:
					frame = TryGetFrame(part.Number);
					if (frame != null) {
						const DbgStackFrameFormatOptions options =
							DbgStackFrameFormatOptions.ShowParameterTypes |
							DbgStackFrameFormatOptions.ShowDeclaringTypes |
							DbgStackFrameFormatOptions.ShowNamespaces |
							DbgStackFrameFormatOptions.ShowIntrinsicTypeKeywords;
						frame.Format(stringBuilderTextColorWriter, options);
					}
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteManagedId:
					if (thread?.ManagedId is ulong mid)
						Write(mid.ToString());
					else
						WriteError();
					break;

				case TracepointMessageKind.WriteProcessId:
					Write("0x");
					Write(boundBreakpoint.Process.Id.ToString("X"));
					break;

				case TracepointMessageKind.WriteProcessName:
					var filename = boundBreakpoint.Process.Filename;
					if (!string.IsNullOrEmpty(filename))
						Write(filename);
					else
						goto case TracepointMessageKind.WriteProcessId;
					break;

				case TracepointMessageKind.WriteThreadId:
					if (thread == null)
						WriteError();
					else {
						Write("0x");
						Write(thread.Id.ToString("X"));
					}
					break;

				case TracepointMessageKind.WriteThreadName:
					var name = thread?.UIName;
					if (name == null)
						WriteError();
					else
						Write(name);
					break;

				default: throw new InvalidOperationException();
				}
			}
		}

		void Write(string s) => output.Append(s);
		void WriteError() => Write("???");

		public override void Write(ITextColorWriter output, DbgCodeBreakpointTrace trace) {
			if (output == null)
				throw new ArgumentNullException(nameof(output));
			var msg = trace.Message ?? string.Empty;
			var parsed = tracepointMessageParser.Parse(msg);
			int pos = 0;
			foreach (var part in parsed.Parts) {
				switch (part.Kind) {
				case TracepointMessageKind.WriteText:
					output.Write(BoxedTextColor.String, msg.Substring(pos, part.Length));
					break;

				case TracepointMessageKind.WriteEvaluatedExpression:
					output.Write(BoxedTextColor.Punctuation, msg.Substring(pos, 1));
					output.Write(BoxedTextColor.Text, msg.Substring(pos + 1, part.Length - 2));
					output.Write(BoxedTextColor.Punctuation, msg.Substring(pos + part.Length - 1, 1));
					break;

				case TracepointMessageKind.WriteAddress:
				case TracepointMessageKind.WriteAppDomainId:
				case TracepointMessageKind.WriteBreakpointAddress:
				case TracepointMessageKind.WriteCaller:
				case TracepointMessageKind.WriteCallerModule:
				case TracepointMessageKind.WriteCallerOffset:
				case TracepointMessageKind.WriteCallerToken:
				case TracepointMessageKind.WriteCallStack:
				case TracepointMessageKind.WriteFunction:
				case TracepointMessageKind.WriteManagedId:
				case TracepointMessageKind.WriteProcessId:
				case TracepointMessageKind.WriteProcessName:
				case TracepointMessageKind.WriteThreadId:
				case TracepointMessageKind.WriteThreadName:
					output.Write(BoxedTextColor.Keyword, msg.Substring(pos, part.Length));
					break;

				default: throw new InvalidOperationException();
				}
				pos += part.Length;
			}
			Debug.Assert(pos == msg.Length);
		}

		sealed class TracepointEvalState : IDisposable {
			public DbgLanguage Language;
			public string TracepointMessage;

			public DbgEvaluationContext Context {
				get => context;
				set {
					context?.Close();
					context = value;
				}
			}
			DbgEvaluationContext context;

			public readonly Dictionary<string, object> ExpressionEvaluatorStates = new Dictionary<string, object>(StringComparer.Ordinal);

			public object GetExpressionEvaluatorState(string expression) {
				if (ExpressionEvaluatorStates.TryGetValue(expression, out var state))
					return state;
				state = Language.ExpressionEvaluator.CreateExpressionEvaluatorState();
				ExpressionEvaluatorStates[expression] = state;
				return state;
			}

			public void Dispose() {
				Language = null;
				TracepointMessage = null;
				Context = null;
				ExpressionEvaluatorStates.Clear();
			}
		}

		TracepointEvalState GetTracepointEvalState(DbgBoundCodeBreakpoint boundBreakpoint, DbgLanguage language, DbgStackFrame frame, string tracepointMessage, CancellationToken cancellationToken) {
			var state = boundBreakpoint.GetOrCreateData<TracepointEvalState>();
			if (state.Language != language || state.TracepointMessage != tracepointMessage) {
				state.Language = language;
				state.TracepointMessage = tracepointMessage;
				state.Context = language.CreateContext(frame, cancellationToken: cancellationToken);
				state.ExpressionEvaluatorStates.Clear();
			}
			return state;
		}

		void Write(DbgEvaluationContext context, DbgStackFrame frame, DbgLanguage language, DbgEvaluationResult evalRes, CancellationToken cancellationToken) {
			if (evalRes.Error != null) {
				Write("<<<");
				Write(PredefinedEvaluationErrorMessagesHelper.GetErrorMessage(evalRes.Error));
				Write(">>>");
			}
			else {
				var options = GetValueFormatterOptions(isDisplay: true);
				const CultureInfo cultureInfo = null;
				language.ValueFormatter.Format(context, frame, stringBuilderTextColorWriter, evalRes.Value, options, cultureInfo, cancellationToken);
				evalRes.Value.Close();
			}
		}

		DbgValueFormatterOptions GetValueFormatterOptions(bool isDisplay) {
			var options = DbgValueFormatterOptions.FuncEval | DbgValueFormatterOptions.ToString;
			if (isDisplay)
				options |= DbgValueFormatterOptions.Display;
			if (!debuggerSettings.UseHexadecimal)
				options |= DbgValueFormatterOptions.Decimal;
			if (debuggerSettings.UseDigitSeparators)
				options |= DbgValueFormatterOptions.DigitSeparators;
			if (dbgEvalFormatterSettings.ShowNamespaces)
				options |= DbgValueFormatterOptions.Namespaces;
			if (dbgEvalFormatterSettings.ShowIntrinsicTypeKeywords)
				options |= DbgValueFormatterOptions.IntrinsicTypeKeywords;
			if (dbgEvalFormatterSettings.ShowTokens)
				options |= DbgValueFormatterOptions.Tokens;
			return options;
		}
	}
}
