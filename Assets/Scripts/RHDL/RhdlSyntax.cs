using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DLS.RHDL
{
	// ---- Tokens ----

	internal enum TokenKind
	{
		Name,
		Number,
		String,
		Op,
		Newline,
		Indent,
		Dedent,
		End
	}

	internal readonly struct Token
	{
		public readonly TokenKind Kind;
		public readonly string Text;
		public readonly long Value;
		public readonly int Line;
		public readonly int Column;

		public Token(TokenKind kind, string text, int line, int column, long value = 0)
		{
			Kind = kind;
			Text = text;
			Line = line;
			Column = column;
			Value = value;
		}

		public bool Is(string op) => (Kind == TokenKind.Op || Kind == TokenKind.Name) && Text == op;

		public override string ToString() => Kind switch
		{
			TokenKind.Newline => "end of line",
			TokenKind.Indent => "indented block",
			TokenKind.Dedent => "end of block",
			TokenKind.End => "end of file",
			TokenKind.String => "string",
			_ => $"'{Text}'"
		};
	}

	internal sealed class RhdlSyntaxException : Exception
	{
		public readonly int Line;
		public readonly int Column;

		public RhdlSyntaxException(int line, int column, string message) : base(message)
		{
			Line = line;
			Column = column;
		}
	}

	// Splits source text into tokens the way Python does: indentation at the start of a
	// line becomes INDENT/DEDENT tokens, and line breaks inside brackets are ignored.
	internal static class RhdlLexer
	{
		const int TabWidth = 4;

		static readonly string[] Operators =
		{
			"->", "==", "!=", "<=", ">=", "<<", ">>", "**", "//",
			"(", ")", "[", "]", ",", ":", ".", "=", "+", "-", "*", "/", "%", "&", "|", "^", "~", "<", ">", "@", "{", "}", ";"
		};

		public static List<Token> Tokenize(string source)
		{
			List<Token> tokens = new();
			Stack<int> indents = new();
			indents.Push(0);

			string text = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
			int pos = 0;
			int line = 1;
			int lineStart = 0;
			int depth = 0;
			bool atLineStart = true;

			while (pos < text.Length)
			{
				if (atLineStart && depth > 0)
				{
					// A line continued inside brackets: indentation means nothing.
					atLineStart = false;
				}

				if (atLineStart && depth == 0)
				{
					// Measure the indentation of a new logical line. Lines holding only
					// whitespace or a comment do not affect the block structure.
					int width = 0;
					int scan = pos;
					while (scan < text.Length && (text[scan] == ' ' || text[scan] == '\t'))
					{
						width = text[scan] == '\t' ? (width / TabWidth + 1) * TabWidth : width + 1;
						scan++;
					}

					if (scan >= text.Length) break;
					if (text[scan] == '\n' || text[scan] == '#')
					{
						while (scan < text.Length && text[scan] != '\n') scan++;
						pos = scan + 1;
						line++;
						lineStart = pos;
						continue;
					}

					int column = scan - lineStart + 1;
					if (width > indents.Peek())
					{
						indents.Push(width);
						tokens.Add(new Token(TokenKind.Indent, string.Empty, line, column));
					}
					else
					{
						while (width < indents.Peek())
						{
							indents.Pop();
							tokens.Add(new Token(TokenKind.Dedent, string.Empty, line, column));
						}

						if (width != indents.Peek())
						{
							throw new RhdlSyntaxException(line, column, "This line's indentation does not match any enclosing block.");
						}
					}

					pos = scan;
					atLineStart = false;
				}

				char c = text[pos];
				int col = pos - lineStart + 1;

				if (c == '\n')
				{
					if (depth == 0 && tokens.Count > 0 && tokens[^1].Kind != TokenKind.Newline)
					{
						tokens.Add(new Token(TokenKind.Newline, string.Empty, line, col));
					}

					pos++;
					line++;
					lineStart = pos;
					atLineStart = true;
					continue;
				}

				if (c == ' ' || c == '\t')
				{
					pos++;
					continue;
				}

				if (c == '#')
				{
					while (pos < text.Length && text[pos] != '\n') pos++;
					continue;
				}

				if (c == '\\' && pos + 1 < text.Length && text[pos + 1] == '\n')
				{
					// Explicit line continuation.
					pos += 2;
					line++;
					lineStart = pos;
					continue;
				}

				if (char.IsLetter(c) || c == '_')
				{
					int start = pos;
					while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
					tokens.Add(new Token(TokenKind.Name, text.Substring(start, pos - start), line, col));
					continue;
				}

				if (char.IsDigit(c))
				{
					int start = pos;
					while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] == '_')) pos++;
					string raw = text.Substring(start, pos - start);
					if (!TryParseNumber(raw, out long value))
					{
						throw new RhdlSyntaxException(line, col, $"'{raw}' is not a valid number. Use decimal, 0b1010, 0o17 or 0xFF.");
					}

					tokens.Add(new Token(TokenKind.Number, raw, line, col, value));
					continue;
				}

				if (c == '"' || c == '\'')
				{
					bool triple = pos + 2 < text.Length && text[pos + 1] == c && text[pos + 2] == c;
					string quote = triple ? new string(c, 3) : c.ToString();
					int startLine = line;
					pos += quote.Length;
					StringBuilder content = new();
					while (true)
					{
						if (pos >= text.Length) throw new RhdlSyntaxException(startLine, col, "This string is never closed.");
						if (string.CompareOrdinal(text, pos, quote, 0, quote.Length) == 0)
						{
							pos += quote.Length;
							break;
						}

						if (text[pos] == '\n')
						{
							if (!triple) throw new RhdlSyntaxException(startLine, col, "This string is never closed.");
							line++;
							lineStart = pos + 1;
						}

						content.Append(text[pos]);
						pos++;
					}

					tokens.Add(new Token(TokenKind.String, content.ToString(), startLine, col));
					continue;
				}

				string op = null;
				foreach (string candidate in Operators)
				{
					if (string.CompareOrdinal(text, pos, candidate, 0, candidate.Length) == 0)
					{
						op = candidate;
						break;
					}
				}

				if (op == null)
				{
					throw new RhdlSyntaxException(line, col, $"Unexpected character '{c}'.");
				}

				if (op is "(" or "[" or "{") depth++;
				else if (op is ")" or "]" or "}") depth = Math.Max(0, depth - 1);

				tokens.Add(new Token(TokenKind.Op, op, line, col));
				pos += op.Length;
			}

			int endColumn = pos - lineStart + 1;
			if (tokens.Count > 0 && tokens[^1].Kind != TokenKind.Newline)
			{
				tokens.Add(new Token(TokenKind.Newline, string.Empty, line, endColumn));
			}

			while (indents.Count > 1)
			{
				indents.Pop();
				tokens.Add(new Token(TokenKind.Dedent, string.Empty, line, endColumn));
			}

			tokens.Add(new Token(TokenKind.End, string.Empty, line, endColumn));
			return tokens;
		}

		static bool TryParseNumber(string raw, out long value)
		{
			value = 0;
			string digits = raw.Replace("_", string.Empty);
			int numberBase = 10;
			if (digits.Length > 2 && digits[0] == '0')
			{
				char prefix = char.ToLowerInvariant(digits[1]);
				if (prefix == 'x') numberBase = 16;
				else if (prefix == 'b') numberBase = 2;
				else if (prefix == 'o') numberBase = 8;
				if (numberBase != 10) digits = digits.Substring(2);
			}

			if (digits.Length == 0) return false;
			if (numberBase == 10) return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);

			try
			{
				value = Convert.ToInt64(digits, numberBase);
				return value >= 0;
			}
			catch (Exception)
			{
				return false;
			}
		}
	}

	// ---- Syntax tree ----

	internal abstract class Node
	{
		public int Line;
		public int Column;
	}

	internal abstract class Expr : Node { }

	internal sealed class NumberExpr : Expr
	{
		public long Value;
		public string Raw;
		// Width implied by the literal's spelling: 0b0010 is 4 bits, 0x0F is 8 bits. 0 if none.
		public int SpelledWidth;
	}

	internal sealed class NameExpr : Expr
	{
		public string Name;
	}

	internal sealed class AttributeExpr : Expr
	{
		public Expr Target;
		public string Name;
	}

	internal sealed class IndexExpr : Expr
	{
		public Expr Target;
		public Expr Index;
	}

	internal sealed class SliceExpr : Expr
	{
		public Expr Target;
		public Expr Low;
		public Expr High;
	}

	internal sealed class CallArgument
	{
		public string Keyword;
		public Expr Value;
		public int Line;
		public int Column;
	}

	internal sealed class CallExpr : Expr
	{
		public Expr Function;
		public List<CallArgument> Arguments = new();
	}

	internal sealed class UnaryExpr : Expr
	{
		public string Op;
		public Expr Operand;
	}

	internal sealed class BinaryExpr : Expr
	{
		public string Op;
		public Expr Left;
		public Expr Right;
	}

	internal sealed class ConditionalExpr : Expr
	{
		public Expr Condition;
		public Expr WhenTrue;
		public Expr WhenFalse;
	}

	internal sealed class ListExpr : Expr
	{
		public List<Expr> Items = new();
	}

	internal sealed class PortSyntax : Node
	{
		public string Name;
		// 0 when no type was written (a single bit).
		public int Width;
	}

	internal abstract class Statement : Node { }

	internal sealed class AssignStatement : Statement
	{
		public List<Expr> Targets = new();
		public int AnnotatedWidth;
		public Expr Value;
	}

	internal sealed class DeclareStatement : Statement
	{
		public string Name;
		public int Width;
	}

	internal sealed class ExpressionStatement : Statement
	{
		public Expr Value;
	}

	internal sealed class ForStatement : Statement
	{
		public string Variable;
		public Expr Start;
		public Expr Stop;
		public Expr Step;
		public List<Statement> Body = new();
	}

	internal sealed class CircuitSyntax : Node
	{
		public string Name;
		public string Doc;
		public List<PortSyntax> Inputs = new();
		public List<PortSyntax> Outputs = new();
		public List<Statement> Body = new();
	}

	internal sealed class ModuleSyntax
	{
		public List<AssignStatement> Constants = new();
		public List<CircuitSyntax> Circuits = new();
	}

	// Recursive-descent parser. Operator precedence follows Python, loosest first:
	// x if c else y, or, and, not, comparisons, |, ^, &, << >>, + -, ~, then calls,
	// indexing and attributes.
	internal sealed class RhdlParser
	{
		public const int MaxWidth = 32;

		// Brackets, calls, '~', 'not' and 'x if c else y' inside each other. Far more than any
		// circuit needs, and it keeps a pasted or generated expression from exhausting the stack.
		public const int MaxNesting = 100;

		readonly List<Token> tokens;
		int position;
		int nesting;

		RhdlParser(List<Token> tokens) => this.tokens = tokens;

		public static ModuleSyntax Parse(string source)
		{
			RhdlParser parser = new(RhdlLexer.Tokenize(source));
			return parser.ParseModule();
		}

		Token Current => tokens[position];
		Token Peek(int offset = 1) => tokens[Math.Min(position + offset, tokens.Count - 1)];

		Token Advance()
		{
			Token token = tokens[position];
			if (position < tokens.Count - 1) position++;
			return token;
		}

		bool Accept(string op)
		{
			if (!Current.Is(op)) return false;
			Advance();
			return true;
		}

		Token Expect(string op, string what = null)
		{
			if (Current.Is(op)) return Advance();
			throw Error(Current, $"Expected '{op}'{(what == null ? string.Empty : " " + what)} but found {Current}.");
		}

		Token ExpectKind(TokenKind kind, string message)
		{
			if (Current.Kind == kind) return Advance();
			throw Error(Current, message + $" Found {Current}.");
		}

		static RhdlSyntaxException Error(Token token, string message) => new(token.Line, token.Column, message);

		ModuleSyntax ParseModule()
		{
			ModuleSyntax module = new();
			while (Current.Kind != TokenKind.End)
			{
				if (Current.Kind == TokenKind.Newline)
				{
					Advance();
					continue;
				}

				if (Current.Kind == TokenKind.Indent)
				{
					throw Error(Current, "Unexpected indentation. Only lines inside a circuit are indented.");
				}

				if (Current.Is("circuit"))
				{
					module.Circuits.Add(ParseCircuit());
					continue;
				}

				if (Current.Kind == TokenKind.String)
				{
					// A module docstring.
					Advance();
					ExpectKind(TokenKind.Newline, "Expected the end of the line after the text.");
					continue;
				}

				if (Current.Kind == TokenKind.Name && Peek().Is("="))
				{
					Statement statement = ParseSimpleStatement();
					module.Constants.Add((AssignStatement)statement);
					continue;
				}

				if (Current.Is("end"))
				{
					throw Error(Current, "RHDL no longer uses 'end': a circuit ends where its indented block ends.");
				}

				if (Current.Is("def") || Current.Is("chip") || Current.Is("module"))
				{
					throw Error(Current, $"Use 'circuit Name(inputs) -> (outputs):' instead of '{Current.Text}'.");
				}

				throw Error(Current, "Expected 'circuit Name(inputs) -> (outputs):' or a constant such as 'WIDTH = 4'.");
			}

			return module;
		}

		CircuitSyntax ParseCircuit()
		{
			Token keyword = Expect("circuit");
			Token name = ExpectKind(TokenKind.Name, "Expected the circuit's name after 'circuit'.");
			CircuitSyntax circuit = new() { Name = name.Text, Line = keyword.Line, Column = keyword.Column };

			if (!Current.Is("("))
			{
				throw Error(Current, $"Expected '(' with the inputs, as in 'circuit {name.Text}(A, B) -> Y:'.");
			}

			circuit.Inputs = ParsePortList();

			if (Accept("->"))
			{
				circuit.Outputs = Current.Is("(") ? ParsePortList() : new List<PortSyntax> { ParsePort() };
			}

			Expect(":", "at the end of the circuit header");
			circuit.Body = ParseBlock(out circuit.Doc);
			return circuit;
		}

		List<PortSyntax> ParsePortList()
		{
			Expect("(");
			List<PortSyntax> ports = new();
			while (!Current.Is(")"))
			{
				ports.Add(ParsePort());
				if (!Accept(",")) break;
			}

			Expect(")", "to close the pin list");
			return ports;
		}

		PortSyntax ParsePort()
		{
			Token name = ExpectKind(TokenKind.Name, "Expected a pin name.");
			PortSyntax port = new() { Name = name.Text, Line = name.Line, Column = name.Column };
			// In 'circuit X(A) -> Y:' the colon ends the header; it only starts a type when a
			// type name follows.
			if (Current.Is(":") && Peek().Kind == TokenKind.Name && TryParseTypeName(Peek().Text, out _))
			{
				Advance();
				port.Width = ParseType();
			}
			else if (Current.Is(":") && Peek().Kind == TokenKind.Name)
			{
				throw Error(Peek(), $"Unknown type '{Peek().Text}'. Use bit, u4 or u8.");
			}
			return port;
		}

		// bit, u1 ... u32
		int ParseType()
		{
			Token type = ExpectKind(TokenKind.Name, "Expected a type such as bit, u4 or u8.");
			if (TryParseTypeName(type.Text, out int width)) return width;
			throw Error(type, $"Unknown type '{type.Text}'. Use bit, u4, u8 or another uN up to u{MaxWidth}.");
		}

		public static bool TryParseTypeName(string text, out int width)
		{
			width = 0;
			if (text == "bit")
			{
				width = 1;
				return true;
			}

			if (text.Length >= 2 && text[0] == 'u' && int.TryParse(text.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out width))
			{
				return width >= 1 && width <= MaxWidth && text[1] != '0';
			}

			return false;
		}

		List<Statement> ParseBlock(out string doc)
		{
			doc = null;
			ExpectKind(TokenKind.Newline, "Expected a new line after ':'.");
			ExpectKind(TokenKind.Indent, "Expected an indented block after ':'.");

			if (Current.Kind == TokenKind.String)
			{
				doc = Advance().Text.Trim();
				ExpectKind(TokenKind.Newline, "Expected the end of the line after the text.");
			}

			List<Statement> body = new();
			while (Current.Kind != TokenKind.Dedent && Current.Kind != TokenKind.End)
			{
				if (Current.Kind == TokenKind.Newline)
				{
					Advance();
					continue;
				}

				Statement statement = ParseStatement();
				if (statement != null) body.Add(statement);
			}

			if (Current.Kind == TokenKind.Dedent) Advance();
			return body;
		}

		Statement ParseStatement()
		{
			Token start = Current;

			if (start.Is("for")) return ParseFor();

			if (start.Is("pass"))
			{
				Advance();
				ExpectKind(TokenKind.Newline, "Expected the end of the line after 'pass'.");
				return null;
			}

			if (start.Kind == TokenKind.String)
			{
				// Stray text, like a docstring further down the body.
				Advance();
				ExpectKind(TokenKind.Newline, "Expected the end of the line after the text.");
				return null;
			}

			if (start.Is("if") || start.Is("while") || start.Is("else") || start.Is("elif"))
			{
				throw Error(start, $"'{start.Text}' blocks would run in sequence, but hardware runs all at once. " +
				                   "Choose between values with 'a if select else b'.");
			}

			if (start.Is("circuit"))
			{
				throw Error(start, "A circuit cannot be defined inside another circuit. Write each circuit in its own file.");
			}

			if (start.Is("return") || start.Is("import") || start.Is("def") || start.Is("class") || start.Is("end"))
			{
				throw Error(start, $"'{start.Text}' is not part of RHDL. Assign outputs by name, e.g. 'Y = A & B'.");
			}

			return ParseSimpleStatement();
		}

		Statement ParseSimpleStatement()
		{
			Token start = Current;

			// NAME: type [= value]
			if (start.Kind == TokenKind.Name && Peek().Is(":"))
			{
				Advance();
				Advance();
				int width = ParseType();
				if (Accept("="))
				{
					AssignStatement annotated = new()
					{
						Line = start.Line,
						Column = start.Column,
						AnnotatedWidth = width,
						Value = ParseExpression()
					};
					annotated.Targets.Add(new NameExpr { Name = start.Text, Line = start.Line, Column = start.Column });
					EndStatement();
					return annotated;
				}

				EndStatement();
				return new DeclareStatement { Name = start.Text, Width = width, Line = start.Line, Column = start.Column };
			}

			Expr first = ParseExpression();
			if (Current.Is(",") || Current.Is("="))
			{
				AssignStatement assign = new() { Line = start.Line, Column = start.Column };
				assign.Targets.Add(first);
				while (Accept(","))
				{
					if (Current.Is("=")) break;
					assign.Targets.Add(ParseExpression());
				}

				if (Current.Is("=") && Peek().Is("="))
				{
					throw Error(Current, "Use a single '=' to drive a signal; '==' compares two values.");
				}

				Expect("=", "after the signal names");
				assign.Value = ParseExpression();
				if (Current.Is("="))
				{
					throw Error(Current, "Chained assignments like 'a = b = c' are not supported. Assign each signal on its own line.");
				}

				EndStatement();
				return assign;
			}

			if (Current.Kind == TokenKind.Op && Current.Text.Length == 1 && Peek().Is("=") && "&|^+-".Contains(Current.Text))
			{
				throw Error(Current, $"'{Current.Text}=' would change a signal after it is set. Write the full expression on one assignment.");
			}

			EndStatement();
			return new ExpressionStatement { Value = first, Line = start.Line, Column = start.Column };
		}

		void EndStatement()
		{
			if (Current.Kind == TokenKind.Newline)
			{
				Advance();
				return;
			}

			if (Current.Is(";")) throw Error(Current, "Write one statement per line; ';' is not needed.");
			throw Error(Current, $"Unexpected {Current} after the end of the statement.");
		}

		Statement ParseFor()
		{
			Token keyword = Expect("for");
			Token variable = ExpectKind(TokenKind.Name, "Expected a loop variable after 'for'.");
			Expect("in");
			Token range = Current;
			if (!range.Is("range"))
			{
				throw Error(range, "Loops repeat hardware a fixed number of times: use 'for i in range(4):'.");
			}

			Advance();
			Expect("(");
			List<Expr> args = new() { ParseExpression() };
			while (Accept(","))
			{
				if (Current.Is(")")) break;
				args.Add(ParseExpression());
			}

			Expect(")");
			if (args.Count > 3) throw Error(range, "range() takes at most three numbers: range(start, stop, step).");
			Expect(":", "at the end of the 'for' line");

			ForStatement loop = new() { Variable = variable.Text, Line = keyword.Line, Column = keyword.Column };
			if (args.Count == 1)
			{
				loop.Start = new NumberExpr { Value = 0, Raw = "0", Line = range.Line, Column = range.Column };
				loop.Stop = args[0];
			}
			else
			{
				loop.Start = args[0];
				loop.Stop = args[1];
				if (args.Count == 3) loop.Step = args[2];
			}

			loop.Body = ParseBlock(out _);
			return loop;
		}

		// ---- Expressions ----

		void EnterNesting()
		{
			if (++nesting > MaxNesting)
			{
				throw Error(Current, $"This expression is nested more than {MaxNesting} levels deep. Split it into named wires.");
			}
		}

		Expr ParseExpression()
		{
			EnterNesting();
			Expr result = ParseExpressionInner();
			nesting--;
			return result;
		}

		Expr ParseExpressionInner()
		{
			Expr value = ParseOr();
			if (Current.Is("if"))
			{
				Token keyword = Advance();
				Expr condition = ParseOr();
				Expect("else", "in 'a if select else b'");
				Expr otherwise = ParseExpression();
				return new ConditionalExpr
				{
					Condition = condition,
					WhenTrue = value,
					WhenFalse = otherwise,
					Line = keyword.Line,
					Column = keyword.Column
				};
			}

			return value;
		}

		Expr ParseOr()
		{
			Expr left = ParseAnd();
			while (Current.Is("or"))
			{
				Token op = Advance();
				left = Binary("or", left, ParseAnd(), op);
			}

			return left;
		}

		Expr ParseAnd()
		{
			Expr left = ParseNot();
			while (Current.Is("and"))
			{
				Token op = Advance();
				left = Binary("and", left, ParseNot(), op);
			}

			return left;
		}

		Expr ParseNot()
		{
			if (Current.Is("not"))
			{
				Token op = Advance();
				EnterNesting();
				Expr operand = ParseNot();
				nesting--;
				return new UnaryExpr { Op = "not", Operand = operand, Line = op.Line, Column = op.Column };
			}

			return ParseComparison();
		}

		static readonly string[] ComparisonOps = { "==", "!=", "<", ">", "<=", ">=" };

		Expr ParseComparison()
		{
			Expr left = ParseBitOr();
			if (IsComparison(Current))
			{
				Token op = Advance();
				Expr right = ParseBitOr();
				if (IsComparison(Current))
				{
					throw Error(Current, "Chained comparisons are not supported. Combine them with 'and': (a < b) and (b < c).");
				}

				return Binary(op.Text, left, right, op);
			}

			return left;

			static bool IsComparison(Token token) => token.Kind == TokenKind.Op && Array.IndexOf(ComparisonOps, token.Text) >= 0;
		}

		Expr ParseBitOr()
		{
			Expr left = ParseBitXor();
			while (Current.Is("|"))
			{
				Token op = Advance();
				left = Binary("|", left, ParseBitXor(), op);
			}

			return left;
		}

		Expr ParseBitXor()
		{
			Expr left = ParseBitAnd();
			while (Current.Is("^"))
			{
				Token op = Advance();
				left = Binary("^", left, ParseBitAnd(), op);
			}

			return left;
		}

		Expr ParseBitAnd()
		{
			Expr left = ParseShift();
			while (Current.Is("&"))
			{
				Token op = Advance();
				left = Binary("&", left, ParseShift(), op);
			}

			return left;
		}

		Expr ParseShift()
		{
			Expr left = ParseArithmetic();
			while (Current.Is("<<") || Current.Is(">>"))
			{
				Token op = Advance();
				left = Binary(op.Text, left, ParseArithmetic(), op);
			}

			return left;
		}

		Expr ParseArithmetic()
		{
			Expr left = ParseUnary();
			while (true)
			{
				if (Current.Is("+") || Current.Is("-"))
				{
					Token op = Advance();
					left = Binary(op.Text, left, ParseUnary(), op);
					continue;
				}

				if (Current.Is("*") || Current.Is("/") || Current.Is("//") || Current.Is("%") || Current.Is("**"))
				{
					throw Error(Current, $"'{Current.Text}' is not supported: RHDL only builds adders and subtractors. " +
					                     "Multiply by a constant power of two with '<<'.");
				}

				return left;
			}
		}

		Expr ParseUnary()
		{
			if (Current.Is("~") || Current.Is("-") || Current.Is("+"))
			{
				Token op = Advance();
				EnterNesting();
				Expr operand = ParseUnary();
				nesting--;
				if (op.Text == "+") return operand;
				return new UnaryExpr { Op = op.Text, Operand = operand, Line = op.Line, Column = op.Column };
			}

			if (Current.Is("!"))
			{
				throw Error(Current, "Use '~' to invert bits, or 'not' for a single bit.");
			}

			return ParsePostfix();
		}

		Expr ParsePostfix()
		{
			Expr value = ParseAtom();
			while (true)
			{
				Token token = Current;
				if (token.Is("("))
				{
					value = ParseCall(value);
					continue;
				}

				if (token.Is("["))
				{
					Advance();
					Expr low = null;
					if (!Current.Is(":")) low = ParseExpression();
					if (Accept(":"))
					{
						Expr high = Current.Is("]") ? null : ParseExpression();
						if (Current.Is(":")) throw Error(Current, "Slices with a step are not supported.");
						Expect("]");
						value = new SliceExpr { Target = value, Low = low, High = high, Line = token.Line, Column = token.Column };
					}
					else
					{
						if (Current.Is(",")) throw Error(Current, "Use a slice such as A[0:4] to select several bits.");
						Expect("]");
						value = new IndexExpr { Target = value, Index = low, Line = token.Line, Column = token.Column };
					}

					continue;
				}

				if (token.Is("."))
				{
					Advance();
					Token name = ExpectKind(TokenKind.Name, "Expected a pin name after '.'.");
					value = new AttributeExpr { Target = value, Name = name.Text, Line = name.Line, Column = name.Column };
					continue;
				}

				return value;
			}
		}

		Expr ParseCall(Expr function)
		{
			Token open = Expect("(");
			CallExpr call = new() { Function = function, Line = function.Line, Column = function.Column };
			bool sawKeyword = false;
			while (!Current.Is(")"))
			{
				Token start = Current;
				CallArgument argument = new() { Line = start.Line, Column = start.Column };
				if (start.Kind == TokenKind.Name && Peek().Is("=") && !Peek(2).Is("="))
				{
					Advance();
					Advance();
					argument.Keyword = start.Text;
					sawKeyword = true;
				}
				else if (sawKeyword)
				{
					throw Error(start, "Pins given by position must come before pins given by name.");
				}

				argument.Value = ParseExpression();
				call.Arguments.Add(argument);
				if (!Accept(",")) break;
			}

			if (Current.Kind == TokenKind.End || Current.Kind == TokenKind.Newline)
			{
				throw Error(open, "This '(' is never closed.");
			}

			Expect(")", "to close the call");
			return call;
		}

		Expr ParseAtom()
		{
			Token token = Current;
			switch (token.Kind)
			{
				case TokenKind.Number:
					Advance();
					return new NumberExpr
					{
						Value = token.Value,
						Raw = token.Text,
						SpelledWidth = SpelledWidth(token.Text),
						Line = token.Line,
						Column = token.Column
					};

				case TokenKind.Name:
					if (IsReserved(token.Text))
					{
						throw Error(token, $"Expected a value but found the keyword '{token.Text}'.");
					}

					Advance();
					if (token.Text == "True" || token.Text == "False")
					{
						return new NumberExpr { Value = token.Text == "True" ? 1 : 0, Raw = token.Text, SpelledWidth = 1, Line = token.Line, Column = token.Column };
					}

					return new NameExpr { Name = token.Text, Line = token.Line, Column = token.Column };

				case TokenKind.Op when token.Text == "(":
				{
					Advance();
					Expr inner = ParseExpression();
					if (Current.Is(","))
					{
						throw Error(Current, "Tuples are only used on the left of '=' to unpack a component's outputs.");
					}

					Expect(")", "to close the bracket");
					return inner;
				}

				case TokenKind.Op when token.Text == "[":
				{
					Advance();
					ListExpr list = new() { Line = token.Line, Column = token.Column };
					while (!Current.Is("]"))
					{
						list.Items.Add(ParseExpression());
						if (!Accept(",")) break;
					}

					Expect("]", "to close the list");
					return list;
				}

				case TokenKind.String:
					throw Error(token, "Text can only be used as a docstring, on its own line.");

				case TokenKind.Newline:
				case TokenKind.End:
					throw Error(token, "Expected a value before the end of the line.");

				default:
					throw Error(token, $"Expected a value but found {token}.");
			}
		}

		static bool IsReserved(string name) => name is
			"circuit" or "for" or "in" or "if" or "else" or "and" or "or" or "not" or "pass" or
			"while" or "def" or "return" or "import" or "class" or "elif" or "lambda" or "end";

		static int SpelledWidth(string raw)
		{
			string digits = raw.Replace("_", string.Empty);
			if (digits.Length > 2 && digits[0] == '0')
			{
				char prefix = char.ToLowerInvariant(digits[1]);
				if (prefix == 'b') return digits.Length - 2;
				if (prefix == 'x') return (digits.Length - 2) * 4;
			}

			return 0;
		}

		static BinaryExpr Binary(string op, Expr left, Expr right, Token token) =>
			new() { Op = op, Left = left, Right = right, Line = token.Line, Column = token.Column };
	}
}
