﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityTwine.Editor.StoryFormats.Harlowe
{
	public delegate int CodeGenMacro(HarloweTranscoder transcoder, LexerToken[] tokens, int macroTokenIndex, MacroUsage usage);

	public enum MacroUsage
	{
		Inline,
		Line,
		LineAndHook
	}

	public static class BuiltInCodeGenMacros
	{
		// ......................
		public static CodeGenMacro Assignment = (transcoder, tokens, tokenIndex, usage) =>
		{
			LexerToken assignToken = tokens[tokenIndex];

			if (usage == MacroUsage.Inline)
				throw new TwineTranscodingException(string.Format("'{0}' macro cannot be used inside another macro", assignToken.name));

			int start = 1;
			int end = start;
			for (; end < assignToken.tokens.Length; end++)
			{
				LexerToken token = assignToken.tokens[end];
				if (token.type == "comma")
				{
					transcoder.GeneratedAssignment(assignToken.name.ToLower(), assignToken.tokens, start, end - 1);
					transcoder.Code.Buffer.Append("; ");
					start = end + 1;
				}
			}
			if (start < end)
			{
				transcoder.GeneratedAssignment(assignToken.name.ToLower(), assignToken.tokens, start, end - 1);
				transcoder.Code.Buffer.Append(";");
			}

			transcoder.Code.Buffer.AppendLine();
			
			return tokenIndex;
		};

		// ......................
		public static CodeGenMacro Conditional = (transcoder, tokens, tokenIndex, usage) =>
		{
			LexerToken token = tokens[tokenIndex];

			if (usage == MacroUsage.Line)
				throw new TwineTranscodingException("'" + token.name + "' must be followed by a Harlowe-hook.");
			if (usage == MacroUsage.Inline)
				throw new TwineTranscodingException("'" + token.name + "' cannot be used inline.");

			transcoder.Code.Buffer.Append(
				token.name == "elseif" ? "else if" :
				token.name == "else" ? "else" :
				"if");

			if (token.name != "else")
			{
				transcoder.Code.Buffer.Append("(");
				if (token.name == "unless")
					transcoder.Code.Buffer.Append("!(");
				transcoder.GenerateExpression(token.tokens, start: 1);
				if (token.name == "unless")
					transcoder.Code.Buffer.Append(")");
				transcoder.Code.Buffer.AppendLine(") {");
			}
			else
				transcoder.Code.Buffer.AppendLine(" {");

			transcoder.Code.Indentation++;

			// Advance to hook
			tokenIndex++;

			LexerToken hookToken = tokens[tokenIndex];
			transcoder.GenerateBody(hookToken.tokens);

			transcoder.Code.Indentation--;
			transcoder.Code.Indent();
			transcoder.Code.Buffer.AppendLine("}");

			// Skip any trailing line breaks and whitespace before the next else or elseif
			if (token.name != "else")
			{
				int i = tokenIndex + 1;
				while (i < tokens.Length)
				{
					if (tokens[i].type != "br" && tokens[i].type != "whitespace")
					{
						// Jump to the position before this macro. Otherwise we'll just continue as normal
						if (tokens[i].type == "macro" && (tokens[i].name == "elseif" || tokens[i].name == "else"))
							tokenIndex = i - 1;

						break;
					}
					else
						i++;
				}
			}

			return tokenIndex;
		};

		// ......................
		public static CodeGenMacro Link = (transcoder, tokens, tokenIndex, usage) =>
		{
			LexerToken linkToken = tokens[tokenIndex];

			transcoder.Code.Buffer.AppendFormat("yield return link(name: null, text: ");
			
			// Text
			int start = 1;
			int end = start;
			for (; end < linkToken.tokens.Length; end++)
				if (linkToken.tokens[end].type == "comma")
					break;
			transcoder.GenerateExpression(linkToken.tokens, start: start, end: end - 1);

			// Passage
			transcoder.Code.Buffer.Append(", passageName: ");
			start = ++end;
			for (; end < linkToken.tokens.Length; end++)
				if (linkToken.tokens[end].type == "comma")
					break;
			if (start < end)
				transcoder.GenerateExpression(linkToken.tokens, start: start, end: end - 1);
			else
				transcoder.Code.Buffer.Append("null");

			// Action
			transcoder.Code.Buffer.Append(", action: ");
			if (linkToken.name != "linkgoto")
			{
				if (usage == MacroUsage.LineAndHook)
				{
					tokenIndex++; // advance
					LexerToken hookToken = tokens[tokenIndex];
					transcoder.Code.Buffer.Append(transcoder.GenerateFragment(hookToken.tokens));
				}
				else
					throw new TwineTranscodingException("Link macro must be followed by a Harlowe-hook");
			}
			else
				transcoder.Code.Buffer.Append("null");

			// Done
			transcoder.Code.Buffer.AppendLine(");");

			return tokenIndex;
		};

		// ......................
		public static CodeGenMacro GoTo = (transcoder, tokens, tokenIndex, usage) =>
		{
			if (usage == MacroUsage.Inline)
				throw new TwineTranscodingException("GoTo macro cannot be used inside another macro");

			transcoder.Code.Buffer.Append("yield return abort(goToPassage: ");
			transcoder.GenerateExpression(tokens[tokenIndex].tokens, 1);
			transcoder.Code.Buffer.AppendLine(");");

			return tokenIndex;
		};

		// ......................
		public static CodeGenMacro Hook = (transcoder, tokens, tokenIndex, usage) =>
		{
			if (usage != MacroUsage.LineAndHook)
				throw new TwineTranscodingException("Hook macro must be followed by a Harlowe-hook");

			LexerToken macroToken = tokens[tokenIndex];
			transcoder.Code.Buffer.Append("yield return fragment(");
			transcoder.GenerateExpression(macroToken.tokens, start: 1);
			transcoder.Code.Buffer.Append(", ");

			tokenIndex++; // advance to the hook
			LexerToken hookToken = tokens[tokenIndex];
			transcoder.Code.Buffer.Append(transcoder.GenerateFragment(hookToken.tokens));
			transcoder.Code.Buffer.AppendLine(");");

			return tokenIndex;
		};

		// ......................
		public static CodeGenMacro Print = (transcoder, tokens, tokenIndex, usage) =>
		{
			if (usage != MacroUsage.Inline)
			{
				transcoder.Code.Buffer.Append("yield return text(");
				transcoder.GenerateExpression(tokens[tokenIndex].tokens, 1);
				transcoder.Code.Buffer.AppendLine(");");
			}
			else
				transcoder.Code.Buffer.Append("null");

			return tokenIndex;
		};

		// ......................

		public static CodeGenMacro RuntimeMacro = (transcoder, tokens, tokenIndex, usage) =>
		{
			LexerToken macroToken = tokens[tokenIndex];

			transcoder.Code.Buffer.AppendFormat("Macros.{0}(", HarloweTranscoder.EscapeCSharpWord(macroToken.name));
			transcoder.GenerateExpression(macroToken.tokens, 1);
			transcoder.Code.Buffer.Append(")");

			if (usage != MacroUsage.Inline)
				transcoder.Code.Buffer.AppendLine(";");

			return tokenIndex;
		};
	}
}