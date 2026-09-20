using System;
using System.Collections.Generic;
using UnityEngine;

namespace DLS.Description
{
	public enum ShortcutAction
	{
		MainMenuNewProject,
		MainMenuOpenProject,
		MainMenuSettings,
		MainMenuQuit,
		Save,
		ChipLibrary,
		Preferences,
		CreateNewChip,
		QuitToMainMenu,
		Search,
		Duplicate,
		ToggleGrid,
		ResetCamera,
		Undo,
		Redo,
		SimulationNextStep,
		SimulationPause,
		MirrorHorizontal,
		MirrorVertical
	}

	public struct ShortcutBinding
	{
		public ShortcutAction Action;
		public KeyCode Key;
		public bool Ctrl;
		public bool Shift;
		public bool Alt;

		public ShortcutBinding(ShortcutAction action, KeyCode key, bool ctrl = false, bool shift = false, bool alt = false)
		{
			Action = action;
			Key = key;
			Ctrl = ctrl;
			Shift = shift;
			Alt = alt;
		}

		public bool IsBound => Key != KeyCode.None;

		public bool SameCombination(ShortcutBinding other) =>
			Key == other.Key && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;

		public string ToDisplayString()
		{
			if (!IsBound) return "UNBOUND";

			string result = string.Empty;
			if (Ctrl) result += "Ctrl+";
			if (Shift) result += "Shift+";
			if (Alt) result += "Alt+";
			return result + FormatKey(Key);
		}

		static string FormatKey(KeyCode key)
		{
			return key switch
			{
				KeyCode.Space => "Space",
				KeyCode.Return => "Enter",
				KeyCode.KeypadEnter => "Numpad Enter",
				KeyCode.Backspace => "Backspace",
				KeyCode.Delete => "Delete",
				KeyCode.UpArrow => "Up",
				KeyCode.DownArrow => "Down",
				KeyCode.LeftArrow => "Left",
				KeyCode.RightArrow => "Right",
				_ => key.ToString()
			};
		}
	}

	public struct AppSettings
	{
		public int ResolutionX;
		public int ResolutionY;
		public FullScreenMode fullscreenMode;
		public bool VSyncEnabled;
		public ShortcutBinding[] KeyBindings;

		public static AppSettings Default() =>
			new()
			{
				ResolutionX = 1920,
				ResolutionY = 1080,
				fullscreenMode = FullScreenMode.FullScreenWindow,
				VSyncEnabled = true,
				KeyBindings = CreateDefaultKeyBindings()
			};

		public static AppSettings Normalize(AppSettings settings)
		{
			ShortcutBinding[] defaults = CreateDefaultKeyBindings();
			List<ShortcutBinding> normalized = new(defaults.Length);

			for (int i = 0; i < defaults.Length; i++)
			{
				ShortcutBinding fallback = defaults[i];
				ShortcutBinding binding = fallback;
				if (settings.KeyBindings != null)
				{
					for (int j = 0; j < settings.KeyBindings.Length; j++)
					{
						if (settings.KeyBindings[j].Action == fallback.Action)
						{
							binding = settings.KeyBindings[j];
							break;
						}
					}
				}
				normalized.Add(binding);
			}

			settings.KeyBindings = normalized.ToArray();
			return settings;
		}

		public ShortcutBinding GetBinding(ShortcutAction action)
		{
			if (KeyBindings != null)
			{
				for (int i = 0; i < KeyBindings.Length; i++)
				{
					if (KeyBindings[i].Action == action) return KeyBindings[i];
				}
			}

			ShortcutBinding[] defaults = CreateDefaultKeyBindings();
			for (int i = 0; i < defaults.Length; i++)
			{
				if (defaults[i].Action == action) return defaults[i];
			}
			return new ShortcutBinding(action, KeyCode.None);
		}

		public void SetBinding(ShortcutAction action, ShortcutBinding binding)
		{
			this = Normalize(this);
			binding.Action = action;

			// A command combination belongs to one action at a time.
			if (binding.IsBound)
			{
				for (int i = 0; i < KeyBindings.Length; i++)
				{
					if (KeyBindings[i].Action != action && SameShortcutContext(KeyBindings[i].Action, action) && KeyBindings[i].SameCombination(binding))
					{
						ShortcutBinding cleared = KeyBindings[i];
						cleared.Key = KeyCode.None;
						cleared.Ctrl = false;
						cleared.Shift = false;
						cleared.Alt = false;
						KeyBindings[i] = cleared;
					}
				}
			}

			for (int i = 0; i < KeyBindings.Length; i++)
			{
				if (KeyBindings[i].Action == action)
				{
					KeyBindings[i] = binding;
					return;
				}
			}
		}

		public void ResetKeyBindings() => KeyBindings = CreateDefaultKeyBindings();

		static bool SameShortcutContext(ShortcutAction a, ShortcutAction b)
		{
			bool aMainMenu = a is ShortcutAction.MainMenuNewProject or ShortcutAction.MainMenuOpenProject or ShortcutAction.MainMenuSettings or ShortcutAction.MainMenuQuit;
			bool bMainMenu = b is ShortcutAction.MainMenuNewProject or ShortcutAction.MainMenuOpenProject or ShortcutAction.MainMenuSettings or ShortcutAction.MainMenuQuit;
			return aMainMenu == bMainMenu;
		}


		public static ShortcutBinding[] CreateDefaultKeyBindings() =>
			new[]
			{
				new ShortcutBinding(ShortcutAction.MainMenuNewProject, KeyCode.N, ctrl: true),
				new ShortcutBinding(ShortcutAction.MainMenuOpenProject, KeyCode.O, ctrl: true),
				new ShortcutBinding(ShortcutAction.MainMenuSettings, KeyCode.S, ctrl: true),
				new ShortcutBinding(ShortcutAction.MainMenuQuit, KeyCode.Q, ctrl: true),

				new ShortcutBinding(ShortcutAction.Save, KeyCode.S, ctrl: true),
				new ShortcutBinding(ShortcutAction.ChipLibrary, KeyCode.L, ctrl: true),
				new ShortcutBinding(ShortcutAction.Preferences, KeyCode.P, ctrl: true),
				new ShortcutBinding(ShortcutAction.CreateNewChip, KeyCode.N, ctrl: true),
				new ShortcutBinding(ShortcutAction.QuitToMainMenu, KeyCode.Q, ctrl: true),
				new ShortcutBinding(ShortcutAction.Search, KeyCode.F, ctrl: true),

				new ShortcutBinding(ShortcutAction.Duplicate, KeyCode.D, shift: true),
				new ShortcutBinding(ShortcutAction.ToggleGrid, KeyCode.G, ctrl: true),
				new ShortcutBinding(ShortcutAction.ResetCamera, KeyCode.R, ctrl: true),
				new ShortcutBinding(ShortcutAction.Undo, KeyCode.Z, ctrl: true),
				new ShortcutBinding(ShortcutAction.Redo, KeyCode.Z, ctrl: true, shift: true),
				new ShortcutBinding(ShortcutAction.SimulationNextStep, KeyCode.Space),
				new ShortcutBinding(ShortcutAction.SimulationPause, KeyCode.Space, ctrl: true),
				new ShortcutBinding(ShortcutAction.MirrorHorizontal, KeyCode.H),
				new ShortcutBinding(ShortcutAction.MirrorVertical, KeyCode.V)
			};
	}
}
