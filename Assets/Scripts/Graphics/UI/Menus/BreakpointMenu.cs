using System;
using DLS.Description;
using DLS.Game;
using DLS.Simulation;
using Seb.Types;
using Seb.Vis;
using Seb.Vis.UI;
using UnityEngine;

namespace DLS.Graphics
{
	// Sets a breakpoint on one pin: the simulation pauses on the step where the pin changes,
	// rises, falls or reaches a value.
	public static class BreakpointMenu
	{
		static readonly UIHandle ID_Value = new("Breakpoint_Value");
		static readonly Func<string, bool> valueValidator = ValidateValue;

		static PinInstance pin;
		static SimPin simPin;
		static string pinLabel;

		public static void Open(PinInstance target)
		{
			pin = target;
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.Breakpoint);
		}

		public static void OnMenuOpened()
		{
			Project project = Project.ActiveProject;
			simPin = project == null || pin == null ? null : ResolveSimPin(project.ViewedChip.SimChip, pin);
			pinLabel = pin == null ? "PIN" : PinLabel(pin);

			Breakpoint existing = simPin == null ? null : SimulationBreakpoints.Find(simPin);
			uint value = existing != null && existing.Condition == BreakCondition.Equals ? existing.Value : 0;
			UI.GetInputFieldState(ID_Value).SetText(value.ToString(), false);
		}

		public static void DrawMenu()
		{
			MenuHelper.DrawBackgroundOverlay();
			DrawSettings.UIThemeDLS theme = DrawSettings.ActiveUITheme;
			Project project = Project.ActiveProject;

			bool singleBit = pin == null || pin.bitCount == PinBitCount.Bit1;
			Breakpoint existing = simPin == null ? null : SimulationBreakpoints.Find(simPin);

			float buttonHeight = MobileUI.IsActive ? 4.4f : DrawSettings.ButtonHeight * 1.15f;
			float width = MobileUI.IsActive ? Mathf.Min(MobileUI.SafeRectUI.width - 2f, 56f) : 30f;
			Vector2 topLeft = MobileUI.IsActive
				? new Vector2(MobileUI.SafeRectUI.center.x - width / 2, MobileUI.SafeRectUI.yMax - 6.4f)
				: UI.Centre + new Vector2(-width / 2, 13f);

			Draw.ID panelID = UI.ReservePanel();
			using (UI.BeginBoundsScope(true))
			{
				RewiredUI.DrawSectionHeader("BREAKPOINT  /  " + pinLabel, topLeft, width, true);
				Vector2 pos = UI.PrevBounds.BottomLeft + Vector2.down * 1.2f;

				string current = simPin == null ? "This pin is not part of the running simulation." : existing == null ? "Pause the simulation when this pin:" : "Now: pause when " + existing.Describe();
				RewiredUI.DrawLabel(current, pos, false, 0.66f);
				pos += Vector2.down * 1.4f;

				Vector2 size = new(width, buttonHeight);
				const float gap = 0.5f;
				bool canSet = simPin != null;

				bool change = UI.Button("CHANGES", theme.ButtonTheme, pos, size, canSet, false, false, Anchor.TopLeft);
				pos = UI.PrevBounds.BottomLeft + Vector2.down * gap;

				bool rise = false, fall = false;
				if (singleBit)
				{
					float half = (width - gap) / 2;
					rise = UI.Button("RISES 0->1", theme.ButtonTheme, pos, new Vector2(half, buttonHeight), canSet, false, false, Anchor.TopLeft);
					fall = UI.Button("FALLS 1->0", theme.ButtonTheme, pos + Vector2.right * (half + gap), new Vector2(half, buttonHeight), canSet, false, false, Anchor.TopLeft);
					pos = UI.PrevBounds.BottomLeft + new Vector2(-(half + gap), -gap);
				}

				float fieldWidth = width * 0.35f;
				InputFieldTheme fieldTheme = theme.ChipNameInputField;
				fieldTheme.fontSize = theme.FontSizeRegular;
				InputFieldState valueField = UI.InputField(ID_Value, fieldTheme, pos, new Vector2(fieldWidth, buttonHeight), "0", Anchor.TopLeft, 1, valueValidator);
				bool equals = UI.Button("EQUALS THIS VALUE", theme.ButtonTheme, pos + Vector2.right * (fieldWidth + gap), new Vector2(width - fieldWidth - gap, buttonHeight), canSet, false, false, Anchor.TopLeft);
				pos += Vector2.down * (buttonHeight + gap * 3);

				float halfWidth = (width - gap) / 2;
				bool remove = UI.Button("REMOVE", theme.ButtonTheme, pos, new Vector2(halfWidth, buttonHeight), existing != null, false, false, Anchor.TopLeft);
				bool close = UI.Button("CLOSE", theme.ButtonTheme, pos + Vector2.right * (halfWidth + gap), new Vector2(halfWidth, buttonHeight), true, false, false, Anchor.TopLeft);

				MenuHelper.DrawReservedMenuPanel(panelID, UI.GetCurrentBoundsScope());

				if (change) SetAndClose(project, BreakCondition.Change, 0);
				else if (rise) SetAndClose(project, BreakCondition.Rising, 0);
				else if (fall) SetAndClose(project, BreakCondition.Falling, 0);
				else if (equals)
				{
					uint.TryParse(valueField.text, out uint value);
					SetAndClose(project, BreakCondition.Equals, value);
				}
				else if (remove)
				{
					SimulationBreakpoints.Remove(simPin);
					UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
				}
				else if (close || KeyboardShortcuts.CancelShortcutTriggered) UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
			}
		}

		static void SetAndClose(Project project, BreakCondition condition, uint value)
		{
			SimulationBreakpoints.Set(new Breakpoint(pinLabel, simPin, project.rootSimChip, (int)pin.bitCount, condition, value));
			UIDrawer.SetActiveMenu(UIDrawer.MenuType.None);
		}

		static string PinLabel(PinInstance p)
		{
			string name = string.IsNullOrWhiteSpace(p.Name) ? "PIN" : p.Name;
			return p.parent is SubChipInstance chip ? $"{chip.Description.Name}.{name}".ToUpperInvariant() : name.ToUpperInvariant();
		}

		// Finds the simulation pin behind an editor pin of the chip being viewed.
		static SimPin ResolveSimPin(SimChip viewed, PinInstance p)
		{
			if (viewed == null) return null;

			switch (p.parent)
			{
				case DevPinInstance devPin:
					return Find(viewed.InputPins, devPin.ID) ?? Find(viewed.OutputPins, devPin.ID);
				case SubChipInstance subChip:
				{
					(bool found, SimChip sim) = viewed.TryGetSubChipFromID(subChip.ID);
					if (!found) return null;
					return Find(sim.OutputPins, p.Address.PinID) ?? Find(sim.InputPins, p.Address.PinID);
				}
				default:
					return null;
			}

			static SimPin Find(SimPin[] pins, int id) => Array.Find(pins, s => s.ID == id);
		}

		static bool ValidateValue(string s) => string.IsNullOrEmpty(s) || (s.Length <= 5 && uint.TryParse(s, out _));
	}
}
