using System;
using Seb.Helpers;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	[Flags]
	public enum RhdlEditorCommand
	{
		None = 0,
		Save = 1,
		Build = 2
	}

	/// <summary>
	/// A document-oriented text editor used by RHDL Studio.
	/// Unlike UI.InputField, the entire source is one text buffer, so selection,
	/// clipboard operations and navigation can cross line boundaries.
	/// </summary>
	public sealed class RhdlCodeEditor
	{
		const float RowHeight = 1.85f;
		const float NumberWidth = 4.2f;
		const float TextPad = 0.45f;
		const string Indent = "  ";

		readonly UIHandle scrollID = new("RHDL_DocumentScroll");
		readonly UI.ScrollViewDrawElementFunc drawLineCallback;

		string text = string.Empty;
		string[] lines = { string.Empty };
		int[] lineStarts = { 0 };
		bool cacheDirty = true;

		int caret;
		int anchor;
		int preferredColumn = -1;
		bool focused;
		bool mouseSelecting;
		float lastInputTime;

		InputFieldTheme activeTheme;
		RhdlEditorCommand commands;

		public RhdlCodeEditor()
		{
			drawLineCallback = DrawLine;
		}

		public string Text => text;
		public bool HasSelection => caret != anchor;
		public int SelectionMin => Math.Min(caret, anchor);
		public int SelectionMax => Math.Max(caret, anchor);
		public int LineCount
		{
			get
			{
				EnsureLineCache();
				return lines.Length;
			}
		}

		public void SetText(string source)
		{
			text = NormalizeNewlines(source ?? string.Empty);
			caret = Mathf.Clamp(caret, 0, text.Length);
			anchor = caret;
			preferredColumn = -1;
			cacheDirty = true;
			UI.GetScrollbarState(scrollID).scrollY = 0;
		}

		public RhdlEditorCommand Draw(Vector2 topLeft, Vector2 size, ScrollViewTheme scrollTheme, InputFieldTheme textTheme)
		{
			commands = RhdlEditorCommand.None;
			activeTheme = textTheme;
			EnsureLineCache();

			UI.DrawScrollView(
				scrollID,
				topLeft,
				size,
				0.08f,
				Anchor.TopLeft,
				scrollTheme,
				drawLineCallback,
				lines.Length);

			Bounds2D editorBounds = UI.PrevBounds;
			if (InputHelper.IsMouseDownThisFrame(MouseButton.Left) && !UI.MouseInsideBounds(editorBounds))
			{
				focused = false;
				mouseSelecting = false;
			}

			if (InputHelper.IsMouseUpThisFrame(MouseButton.Left)) mouseSelecting = false;

			HandleKeyboard();
			return commands;
		}

		void DrawLine(Vector2 topLeft, float width, int lineIndex, bool isLayoutPass)
		{
			Bounds2D row = Bounds2D.CreateFromTopLeftAndSize(topLeft, new Vector2(width, RowHeight));
			if (!isLayoutPass)
			{
				Color rowCol = lineIndex % 2 == 0 ? RewiredUI.Surface : RewiredUI.SurfaceRaised;
				UI.DrawPanel(row, rowCol);

				UI.DrawText(
					(lineIndex + 1).ToString(),
					activeTheme.font,
					activeTheme.fontSize * 0.86f,
					row.CentreLeft + Vector2.right * 0.45f,
					Anchor.TextCentreLeft,
					RewiredUI.DimText);

				string line = lines[lineIndex];
				float textX = row.Left + NumberWidth + TextPad;
				Vector2 textPos = new(textX, row.Centre.y);

				DrawSelectionForLine(lineIndex, row, textX);
				UI.DrawText(line, activeTheme.font, activeTheme.fontSize, textPos, Anchor.TextCentreLeft, activeTheme.textCol);
				DrawCaretForLine(lineIndex, row, textX);

				HandleMouseForLine(lineIndex, row, textX);
			}

			UI.OverridePreviousBounds(row);
		}

		void DrawSelectionForLine(int lineIndex, Bounds2D row, float textX)
		{
			if (!HasSelection) return;

			int lineStart = lineStarts[lineIndex];
			int lineEnd = lineStart + lines[lineIndex].Length;
			int selectionStart = SelectionMin;
			int selectionEnd = SelectionMax;

			bool touchesLine = selectionEnd > lineStart && selectionStart <= lineEnd;
			bool selectsEmptyNewline = lines[lineIndex].Length == 0 &&
			                         selectionStart <= lineStart &&
			                         selectionEnd > lineStart;
			if (!touchesLine && !selectsEmptyNewline) return;

			int localStart = Mathf.Clamp(selectionStart - lineStart, 0, lines[lineIndex].Length);
			int localEnd = Mathf.Clamp(selectionEnd - lineStart, 0, lines[lineIndex].Length);

			float startWidth = PrefixWidth(lines[lineIndex], localStart);
			float endWidth = PrefixWidth(lines[lineIndex], localEnd);

			// If the selection crosses the newline, leave a visible marker after EOL.
			if (selectionEnd > lineEnd && lineIndex < lines.Length - 1)
				endWidth = Mathf.Max(endWidth, PrefixWidth(lines[lineIndex], lines[lineIndex].Length) + activeTheme.fontSize * 0.45f);

			float width = Mathf.Max(activeTheme.fontSize * 0.12f, endWidth - startWidth);
			UI.DrawPanel(
				new Vector2(textX + startWidth, row.Centre.y),
				new Vector2(width, RowHeight * 0.72f),
				new Color(0.20f, 0.48f, 1f, 0.34f),
				Anchor.CentreLeft);
		}

		void DrawCaretForLine(int lineIndex, Bounds2D row, float textX)
		{
			if (!focused || HasSelection) return;
			if ((int)((Time.time - lastInputTime) / 0.5f) % 2 != 0) return;

			int caretLine = FindLineForIndex(caret);
			if (caretLine != lineIndex) return;

			int column = Mathf.Clamp(caret - lineStarts[lineIndex], 0, lines[lineIndex].Length);
			float x = textX + PrefixWidth(lines[lineIndex], column);
			UI.DrawPanel(
				new Vector2(x, row.Centre.y),
				new Vector2(Mathf.Max(0.08f, activeTheme.fontSize * 0.06f), RowHeight * 0.72f),
				activeTheme.textCol,
				Anchor.Centre);
		}

		void HandleMouseForLine(int lineIndex, Bounds2D row, float textX)
		{
			bool inside = UI.MouseInsideBounds(row);
			if (inside && InputHelper.IsMouseDownThisFrame(MouseButton.Left))
			{
				int index = IndexAtMouseX(lineIndex, textX);
				focused = true;
				mouseSelecting = true;

				if (InputHelper.ShiftIsHeld)
					SetCaret(index, true);
				else
				SetCaret(index, false);
			}

			if (focused && mouseSelecting && inside && InputHelper.IsMouseHeld(MouseButton.Left))
			{
				SetCaret(IndexAtMouseX(lineIndex, textX), true);
			}
		}

		int IndexAtMouseX(int lineIndex, float textX)
		{
			float mouseX = UI.ScreenToUISpace(InputHelper.MousePos).x;
			float target = mouseX - textX;
			string line = lines[lineIndex];

			if (target <= 0) return lineStarts[lineIndex];
			if (target >= PrefixWidth(line, line.Length)) return lineStarts[lineIndex] + line.Length;

			int low = 0;
			int high = line.Length;
			while (low < high)
			{
				int mid = (low + high) / 2;
				float width = PrefixWidth(line, mid);
				if (width < target) low = mid + 1;
				else high = mid;
			}

			int right = Mathf.Clamp(low, 0, line.Length);
			int left = Mathf.Max(0, right - 1);
			float leftWidth = PrefixWidth(line, left);
			float rightWidth = PrefixWidth(line, right);
			int column = Mathf.Abs(target - leftWidth) <= Mathf.Abs(rightWidth - target) ? left : right;
			return lineStarts[lineIndex] + column;
		}

		float PrefixWidth(string line, int charCount)
		{
			charCount = Mathf.Clamp(charCount, 0, line.Length);
			if (charCount == 0) return 0;
			return UI.CalculateTextSize(line.AsSpan(0, charCount), activeTheme.fontSize, activeTheme.font).x;
		}

		void HandleKeyboard()
		{
			if (!focused) return;

			bool ctrl = InputHelper.CtrlIsHeld;
			bool shift = InputHelper.ShiftIsHeld;

			if (ctrl && InputHelper.IsKeyDownThisFrame(KeyCode.A))
			{
				anchor = 0;
				caret = text.Length;
				Touch();
				return;
			}

			if (ctrl && InputHelper.IsKeyDownThisFrame(KeyCode.C))
			{
				InputHelper.CopyToClipboard(HasSelection ? SelectedText() : CurrentLineText(includeNewline: true));
			}

			if (ctrl && InputHelper.IsKeyDownThisFrame(KeyCode.X))
			{
				if (HasSelection)
				InputHelper.CopyToClipboard(DeleteSelection());
				else
				InputHelper.CopyToClipboard(CutCurrentLine());
			}

			if (ctrl && InputHelper.IsKeyDownThisFrame(KeyCode.V))
			{
				ReplaceSelection(NormalizeNewlines(InputHelper.GetClipboardContents() ?? string.Empty));
			}

			if (ctrl && InputHelper.IsKeyDownThisFrame(KeyCode.S))
				commands |= RhdlEditorCommand.Save;

			if (ctrl && shift && InputHelper.IsKeyDownThisFrame(KeyCode.B))
				commands |= RhdlEditorCommand.Build;

			if (InputHelper.IsKeyDownThisFrame(KeyCode.Return) || InputHelper.IsKeyDownThisFrame(KeyCode.KeypadEnter))
			{
				InsertNewlineWithIndent();
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.Tab))
			{
				HandleTab(shift);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.Backspace))
			{
				if (HasSelection) DeleteSelection();
				else if (caret > 0) ReplaceRange(caret - 1, caret, string.Empty);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.Delete))
			{
				if (HasSelection) DeleteSelection();
				else if (caret < text.Length) ReplaceRange(caret, caret + 1, string.Empty);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.LeftArrow))
			{
				MoveHorizontal(-1, shift, ctrl);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.RightArrow))
			{
				MoveHorizontal(1, shift, ctrl);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.UpArrow))
			{
				MoveVertical(-1, shift);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.DownArrow))
			{
				MoveVertical(1, shift);
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.Home))
			{
				if (ctrl) SetCaret(0, shift);
				else
				{
					int line = FindLineForIndex(caret);
					SetCaret(lineStarts[line], shift);
				}
				return;
			}

			if (InputHelper.IsKeyDownThisFrame(KeyCode.End))
			{
				if (ctrl) SetCaret(text.Length, shift);
				else
				{
					int line = FindLineForIndex(caret);
					SetCaret(lineStarts[line] + lines[line].Length, shift);
				}
				return;
			}

			if (!ctrl && !InputHelper.AltIsHeld)
			{
				foreach (char c in InputHelper.InputStringThisFrame)
				{
					if (char.IsControl(c) || char.IsSurrogate(c)) continue;
					ReplaceSelection(c.ToString());
				}
			}
		}

		void InsertNewlineWithIndent()
		{
			int lineIndex = FindLineForIndex(caret);
			string line = lines[lineIndex];
			int column = Mathf.Clamp(caret - lineStarts[lineIndex], 0, line.Length);
			string beforeCaret = line.Substring(0, column);

			int whitespaceCount = 0;
			while (whitespaceCount < beforeCaret.Length && char.IsWhiteSpace(beforeCaret[whitespaceCount])) whitespaceCount++;
			string indent = beforeCaret.Substring(0, whitespaceCount);
			if (beforeCaret.TrimEnd().EndsWith("{")) indent += Indent;

			ReplaceSelection("\n" + indent);
		}

		void HandleTab(bool unindent)
		{
			if (HasSelection)
			{
				int firstLine = FindLineForIndex(SelectionMin);
				int lastLine = FindLineForIndex(Mathf.Max(SelectionMin, SelectionMax - 1));
				TransformLineIndent(firstLine, lastLine, unindent);
				return;
			}

			if (!unindent)
			{
				ReplaceSelection(Indent);
				return;
			}

			int lineIndex = FindLineForIndex(caret);
			int start = lineStarts[lineIndex];
			int remove = 0;
			while (remove < Indent.Length && start + remove < text.Length && text[start + remove] == ' ') remove++;
			if (remove > 0)
			{
				int oldCaret = caret;
				ReplaceRange(start, start + remove, string.Empty);
				SetCaret(Mathf.Max(start, oldCaret - remove), false);
			}
		}

		void TransformLineIndent(int firstLine, int lastLine, bool unindent)
		{
			EnsureLineCache();
			string[] work = (string[])lines.Clone();

			for (int i = firstLine; i <= lastLine; i++)
			{
				if (!unindent)
				{
					work[i] = Indent + work[i];
				}
				else
				{
					int remove = 0;
					while (remove < Indent.Length && remove < work[i].Length && work[i][remove] == ' ') remove++;
					if (remove > 0) work[i] = work[i].Substring(remove);
				}
			}

			text = string.Join("\n", work);
			cacheDirty = true;
			EnsureLineCache();

			anchor = lineStarts[firstLine];
			caret = lineStarts[lastLine] + lines[lastLine].Length;
			Touch();
		}

		void MoveHorizontal(int delta, bool select, bool byWord)
		{
			if (HasSelection && !select && !byWord)
			{
				SetCaret(delta < 0 ? SelectionMin : SelectionMax, false);
				return;
			}

			int target = byWord
				? (delta < 0 ? PreviousWordIndex(caret) : NextWordIndex(caret))
				: Mathf.Clamp(caret + delta, 0, text.Length);
			SetCaret(target, select);
			preferredColumn = -1;
		}

		void MoveVertical(int deltaLine, bool select)
		{
			EnsureLineCache();
			int currentLine = FindLineForIndex(caret);
			int column = caret - lineStarts[currentLine];
			if (preferredColumn < 0) preferredColumn = column;

			int targetLine = Mathf.Clamp(currentLine + deltaLine, 0, lines.Length - 1);
			int targetColumn = Mathf.Min(preferredColumn, lines[targetLine].Length);
			SetCaret(lineStarts[targetLine] + targetColumn, select, preservePreferredColumn: true);
		}

		int PreviousWordIndex(int from)
		{
			int i = Mathf.Clamp(from, 0, text.Length);
			while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
			while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
			return i;
		}

		int NextWordIndex(int from)
		{
			int i = Mathf.Clamp(from, 0, text.Length);
			while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
			while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
			return i;
		}

		void ReplaceSelection(string replacement)
		{
			if (HasSelection)
				ReplaceRange(SelectionMin, SelectionMax, replacement);
			else
				ReplaceRange(caret, caret, replacement);
		}

		string DeleteSelection()
		{
			if (!HasSelection) return string.Empty;
			string removed = SelectedText();
			ReplaceRange(SelectionMin, SelectionMax, string.Empty);
			return removed;
		}

		void ReplaceRange(int start, int end, string replacement)
		{
			start = Mathf.Clamp(start, 0, text.Length);
			end = Mathf.Clamp(end, start, text.Length);
			replacement ??= string.Empty;

			text = text.Remove(start, end - start).Insert(start, replacement);
			caret = start + replacement.Length;
			anchor = caret;
			preferredColumn = -1;
			cacheDirty = true;
			Touch();
		}

		string SelectedText() => text.Substring(SelectionMin, SelectionMax - SelectionMin);

		string CurrentLineText(bool includeNewline)
		{
			EnsureLineCache();
			int line = FindLineForIndex(caret);
			int start = lineStarts[line];
			int length = lines[line].Length;
			if (includeNewline && line < lines.Length - 1) length++;
			return text.Substring(start, length);
		}

		string CutCurrentLine()
		{
			EnsureLineCache();
			int line = FindLineForIndex(caret);
			int start = lineStarts[line];
			int end = start + lines[line].Length;
			if (line < lines.Length - 1) end++;
			else if (line > 0) start--;

			string removed = text.Substring(start, end - start);
			ReplaceRange(start, end, string.Empty);
			return removed;
		}

		void SetCaret(int index, bool select, bool preservePreferredColumn = false)
		{
			index = Mathf.Clamp(index, 0, text.Length);
			if (!select) anchor = index;
			caret = index;
			if (!preservePreferredColumn) preferredColumn = -1;
			Touch();
		}

		void Touch() => lastInputTime = Time.time;

		int FindLineForIndex(int index)
		{
			EnsureLineCache();
			index = Mathf.Clamp(index, 0, text.Length);

			int low = 0;
			int high = lineStarts.Length - 1;
			while (low <= high)
			{
				int mid = (low + high) / 2;
				if (lineStarts[mid] <= index)
				{
					if (mid == lineStarts.Length - 1 || lineStarts[mid + 1] > index) return mid;
					low = mid + 1;
				}
				else
				{
					high = mid - 1;
				}
			}
			return 0;
		}

		void EnsureLineCache()
		{
			if (!cacheDirty) return;

			lines = text.Split('\n');
			if (lines.Length == 0) lines = new[] { string.Empty };

			lineStarts = new int[lines.Length];
			int start = 0;
			for (int i = 0; i < lines.Length; i++)
			{
				lineStarts[i] = start;
				start += lines[i].Length;
				if (i < lines.Length - 1) start++;
			}

			caret = Mathf.Clamp(caret, 0, text.Length);
			anchor = Mathf.Clamp(anchor, 0, text.Length);
			cacheDirty = false;
		}

		static string NormalizeNewlines(string value) =>
			(value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
	}
}
