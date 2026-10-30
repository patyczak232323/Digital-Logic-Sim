using System;
using System.Collections.Generic;
using System.Globalization;

namespace DLS.Tests
{
	// Assembler for the PIX6 example computer (Examples/RHDL/PIX6/README.md).
	// One instruction per line, "label:" on its own line or before an instruction,
	// ';' comments, numbers in decimal or 0x hex, jump targets by label.
	public static class Pix6Assembler
	{
		static readonly Dictionary<string, uint> Plain = new(StringComparer.OrdinalIgnoreCase)
		{
			["NOP"] = 0x0000, ["PLOT"] = 0x5000, ["INCX"] = 0x6000, ["INCY"] = 0x7000,
			["PAT"] = 0xB000, ["SHOW"] = 0xC000, ["CLS"] = 0xD000, ["PLOTX"] = 0xE000, ["HLT"] = 0xF000,
			["TURN"] = 0x0200, ["STEP"] = 0x0300, ["STEPC"] = 0x0400, ["READ"] = 0x0500,
			["DCOL"] = 0x0600, ["ERASE"] = 0x0700, ["KEYS"] = 0x0800, ["RND"] = 0x0A00
		};

		// Instructions with a number: base word and the largest number it takes.
		static readonly Dictionary<string, (uint word, uint max)> WithNumber = new(StringComparer.OrdinalIgnoreCase)
		{
			["SETX"] = (0x1000, 63), ["SETY"] = (0x2000, 63), ["SETC"] = (0x3000, 0xFFF), ["ADDC"] = (0x4000, 0xFFF),
			["SETT"] = (0x0C00, 255), ["GROW"] = (0x0E00, 255)
		};

		static readonly Dictionary<string, uint> Jumps = new(StringComparer.OrdinalIgnoreCase)
		{
			["JMP"] = 0x8000, ["JXNZ"] = 0x9000, ["JYNZ"] = 0xA000,
			["JKEY"] = 0x0900, ["JCZ"] = 0x0B00, ["DJNZ"] = 0x0D00, ["JGROW"] = 0x0F00
		};

		public static uint[] Assemble(string source) => Assemble(source, out _);

		public static uint[] Assemble(string source, out Dictionary<string, int> labels)
		{
			labels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			List<(int line, string op, string operand)> instructions = new();

			string[] lines = source.Replace("\r", "").Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string text = lines[i];
				int comment = text.IndexOf(';');
				if (comment >= 0) text = text.Substring(0, comment);
				text = text.Trim();

				int colon = text.IndexOf(':');
				if (colon >= 0)
				{
					string label = text.Substring(0, colon).Trim();
					if (!labels.TryAdd(label, instructions.Count)) throw Error(i, $"label '{label}' is defined twice");
					text = text.Substring(colon + 1).Trim();
				}

				if (text.Length == 0) continue;
				string[] parts = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
				if (parts.Length > 2) throw Error(i, $"unexpected '{parts[2]}'");
				instructions.Add((i, parts[0], parts.Length > 1 ? parts[1] : null));
			}

			if (instructions.Count > 256) throw new FormatException($"the program has {instructions.Count} words; the ROM holds 256");

			uint[] words = new uint[instructions.Count];
			for (int i = 0; i < instructions.Count; i++)
			{
				(int line, string op, string operand) = instructions[i];
				words[i] = Encode(line, op, operand, labels);
			}

			return words;
		}

		static uint Encode(int line, string op, string operand, Dictionary<string, int> labels)
		{
			if (Plain.TryGetValue(op, out uint plain))
			{
				if (operand != null) throw Error(line, $"{op} takes no operand");
				return plain;
			}

			if (op.Equals("SWAP", StringComparison.OrdinalIgnoreCase))
			{
				if (string.Equals(operand, "B", StringComparison.OrdinalIgnoreCase)) return 0x0100;
				if (string.Equals(operand, "F", StringComparison.OrdinalIgnoreCase)) return 0x0101;
				throw Error(line, "SWAP takes B or F");
			}

			if (operand == null) throw Error(line, $"{op} needs an operand");

			if (WithNumber.TryGetValue(op, out (uint word, uint max) number))
			{
				uint value = ParseNumber(line, operand);
				if (value > number.max) throw Error(line, $"{op} takes 0 to {number.max}, not {value}");
				return number.word | value;
			}

			if (Jumps.TryGetValue(op, out uint jump))
			{
				if (!labels.TryGetValue(operand, out int address)) throw Error(line, $"unknown label '{operand}'");
				return jump | (uint)address;
			}

			throw Error(line, $"unknown instruction '{op}'");
		}

		static uint ParseNumber(int line, string text)
		{
			bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value)
				: uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
			if (!ok) throw Error(line, $"'{text}' is not a number");
			return value;
		}

		static FormatException Error(int line, string message) => new($"line {line + 1}: {message}");
	}
}
