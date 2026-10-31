using System.Linq;
using DLS.Description;
using NUnit.Framework;
using UnityEngine;

namespace DLS.Tests
{
	public class ShortcutSettingsTests
	{
		[Test]
		public void EditingShortcutsHaveTheUsualDefaults()
		{
			AppSettings settings = AppSettings.Default();

			AssertBinding(settings, ShortcutAction.SelectAll, KeyCode.A);
			AssertBinding(settings, ShortcutAction.Copy, KeyCode.C);
			AssertBinding(settings, ShortcutAction.Cut, KeyCode.X);
			AssertBinding(settings, ShortcutAction.Paste, KeyCode.V);
			AssertBinding(settings, ShortcutAction.RedoAlternative, KeyCode.Y);
		}

		[Test]
		public void DefaultEditorShortcutsDoNotOverlap()
		{
			ShortcutBinding[] editor = AppSettings.CreateDefaultKeyBindings()
				.Where(b => b.IsBound && !b.Action.ToString().StartsWith("MainMenu"))
				.ToArray();

			for (int i = 0; i < editor.Length; i++)
			{
				for (int j = i + 1; j < editor.Length; j++)
				{
					Assert.IsFalse(editor[i].SameCombination(editor[j]), $"{editor[i].Action} and {editor[j].Action} share {editor[i].ToDisplayString()}");
				}
			}
		}

		[Test]
		public void SettingsFromAnOlderVersionGetTheNewShortcuts()
		{
			// Saved before Copy/Paste existed: only the older actions are stored
			AppSettings old = AppSettings.Default();
			old.KeyBindings = old.KeyBindings.Where(b => b.Action <= ShortcutAction.MirrorVertical).ToArray();

			AppSettings normalized = AppSettings.Normalize(old);

			AssertBinding(normalized, ShortcutAction.Copy, KeyCode.C);
			AssertBinding(normalized, ShortcutAction.Paste, KeyCode.V);
		}

		[Test]
		public void NewShortcutStartsUnboundIfTheUserAlreadyUsesItsKeys()
		{
			AppSettings old = AppSettings.Default();
			old.KeyBindings = old.KeyBindings.Where(b => b.Action <= ShortcutAction.MirrorVertical).ToArray();
			for (int i = 0; i < old.KeyBindings.Length; i++)
			{
				if (old.KeyBindings[i].Action == ShortcutAction.Duplicate) old.KeyBindings[i] = new ShortcutBinding(ShortcutAction.Duplicate, KeyCode.C, ctrl: true);
			}

			AppSettings normalized = AppSettings.Normalize(old);

			Assert.IsFalse(normalized.GetBinding(ShortcutAction.Copy).IsBound);
			AssertBinding(normalized, ShortcutAction.Duplicate, KeyCode.C);
			AssertBinding(normalized, ShortcutAction.Paste, KeyCode.V);
		}

		static void AssertBinding(AppSettings settings, ShortcutAction action, KeyCode key)
		{
			ShortcutBinding binding = settings.GetBinding(action);
			Assert.AreEqual(key, binding.Key, action.ToString());
			Assert.IsTrue(binding.Ctrl, action + " uses Ctrl");
			Assert.IsFalse(binding.Shift, action + " has no Shift");
			Assert.IsFalse(binding.Alt, action + " has no Alt");
		}
	}
}
