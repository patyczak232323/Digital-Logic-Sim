using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DLS.Tests
{
	// Assembler for the CPU8 example processor (Examples/RHDL/CPU8/README.md); the same rules as
	// Examples/RHDL/CPU8/cpu8asm.py. One instruction per line, "label:" on its own line or before
	// an instruction, ';' or '#' comments, numbers in decimal, 0x hex or 0b binary.
	public static class Cpu8Asm
	{
		enum Format { Op, RegReg, Imm8, Reg, Load, Store, LoadReg, StoreReg, Jump }

		static readonly Dictionary<string, (uint opcode, Format format)> Instructions = new(StringComparer.OrdinalIgnoreCase)
		{
			["NOP"] = (0, Format.Op), ["HLT"] = (1, Format.Op), ["MOV"] = (2, Format.RegReg), ["LDI"] = (3, Format.Imm8),
			["LD"] = (4, Format.Load), ["ST"] = (5, Format.Store), ["LDR"] = (6, Format.LoadReg), ["STR"] = (7, Format.StoreReg),
			["ADD"] = (8, Format.RegReg), ["ADDI"] = (9, Format.Imm8), ["ADC"] = (10, Format.RegReg), ["SUB"] = (11, Format.RegReg),
			["SUBI"] = (12, Format.Imm8), ["SBC"] = (13, Format.RegReg), ["INC"] = (14, Format.Reg), ["DEC"] = (15, Format.Reg),
			["AND"] = (16, Format.RegReg), ["ANDI"] = (17, Format.Imm8), ["OR"] = (18, Format.RegReg), ["ORI"] = (19, Format.Imm8),
			["XOR"] = (20, Format.RegReg), ["XORI"] = (21, Format.Imm8), ["NOT"] = (22, Format.Reg), ["SHL"] = (23, Format.Reg),
			["SHR"] = (24, Format.Reg), ["CMP"] = (25, Format.RegReg), ["CMPI"] = (26, Format.Imm8), ["JMP"] = (27, Format.Jump),
			["JZ"] = (28, Format.Jump), ["JNZ"] = (29, Format.Jump), ["JC"] = (30, Format.Jump), ["JNC"] = (31, Format.Jump)
		};

		public static uint[] Assemble(string source)
		{
			Dictionary<string, int> labels = new(StringComparer.OrdinalIgnoreCase);
			List<(int line, string op, string[] args)> instructions = new();

			string[] lines = source.Replace("\r", "").Split('\n');
			for (int i = 0; i < lines.Length; i++)
			{
				string text = lines[i];
				int comment = text.IndexOfAny(new[] { ';', '#' });
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
				int space = text.IndexOfAny(new[] { ' ', '\t' });
				string op = space < 0 ? text : text.Substring(0, space);
				string[] args = space < 0
					? Array.Empty<string>()
					: text.Substring(space + 1).Split(',').Select(a => a.Trim()).ToArray();
				instructions.Add((i, op, args));
			}

			uint[] words = new uint[instructions.Count];
			for (int n = 0; n < instructions.Count; n++)
			{
				(int line, string op, string[] args) = instructions[n];
				if (!Instructions.TryGetValue(op, out (uint opcode, Format format) ins)) throw Error(line, $"unknown instruction '{op}'");

				uint word = ins.opcode << 11;
				switch (ins.format)
				{
					case Format.Op:
						Expect(line, args, 0);
						break;
					case Format.Reg:
						Expect(line, args, 1);
						word |= Reg(line, args[0]) << 8;
						break;
					case Format.RegReg:
						Expect(line, args, 2);
						word |= Reg(line, args[0]) << 8 | Reg(line, args[1]) << 5;
						break;
					case Format.Imm8:
						Expect(line, args, 2);
						word |= Reg(line, args[0]) << 8 | Number(line, args[1], 0xFF, labels);
						break;
					case Format.Load:
						Expect(line, args, 2);
						word |= Reg(line, args[0]) << 8 | Number(line, Bracket(line, args[1]), 0xFF, labels);
						break;
					case Format.Store:
						Expect(line, args, 2);
						word |= Reg(line, args[1]) << 8 | Number(line, Bracket(line, args[0]), 0xFF, labels);
						break;
					case Format.LoadReg:
						Expect(line, args, 2);
						word |= Reg(line, args[0]) << 8 | Reg(line, Bracket(line, args[1])) << 5;
						break;
					case Format.StoreReg:
						Expect(line, args, 2);
						word |= Reg(line, Bracket(line, args[0])) << 8 | Reg(line, args[1]) << 5;
						break;
					case Format.Jump:
						Expect(line, args, 1);
						word |= Number(line, args[0], 0x7FF, labels);
						break;
				}

				words[n] = word;
			}

			return words;
		}

		static void Expect(int line, string[] args, int count)
		{
			if (args.Length != count) throw Error(line, $"expected {count} operand(s), got {args.Length}");
		}

		static string Bracket(int line, string arg)
		{
			if (!arg.StartsWith("[") || !arg.EndsWith("]")) throw Error(line, $"expected [address], got '{arg}'");
			return arg.Substring(1, arg.Length - 2).Trim();
		}

		static uint Reg(int line, string arg)
		{
			if (arg.Length == 2 && (arg[0] == 'R' || arg[0] == 'r') && arg[1] >= '0' && arg[1] <= '7') return (uint)(arg[1] - '0');
			throw Error(line, $"expected a register R0..R7, got '{arg}'");
		}

		static uint Number(int line, string arg, uint max, Dictionary<string, int> labels)
		{
			uint value;
			if (labels.TryGetValue(arg, out int address)) value = (uint)address;
			else if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(arg.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)) { }
			else if (arg.StartsWith("0b", StringComparison.OrdinalIgnoreCase) && TryParseBinary(arg.Substring(2), out value)) { }
			else if (uint.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out value)) { }
			else throw Error(line, $"expected a number or label, got '{arg}'");

			if (value > max) throw Error(line, $"{arg} is larger than {max}");
			return value;
		}

		static bool TryParseBinary(string digits, out uint value)
		{
			value = 0;
			if (digits.Length == 0 || digits.Length > 32) return false;
			foreach (char ch in digits)
			{
				if (ch != '0' && ch != '1') return false;
				value = value << 1 | (uint)(ch - '0');
			}

			return true;
		}

		static FormatException Error(int line, string message) => new($"line {line + 1}: {message}");
	}
}
