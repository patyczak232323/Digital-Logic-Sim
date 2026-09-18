using System.Collections.Generic;
using Seb.Helpers;
using UnityEngine;

namespace DLS.Simulation
{
	public static class SimKeyboardHelper
	{
		public static readonly KeyCode[] ValidInputKeys =
		{
			KeyCode.A, KeyCode.B, KeyCode.C, KeyCode.D, KeyCode.E, KeyCode.F, KeyCode.G,
			KeyCode.H, KeyCode.I, KeyCode.J, KeyCode.K, KeyCode.L, KeyCode.M, KeyCode.N,
			KeyCode.O, KeyCode.P, KeyCode.Q, KeyCode.R, KeyCode.S, KeyCode.T, KeyCode.U,
			KeyCode.V, KeyCode.W, KeyCode.X, KeyCode.Y, KeyCode.Z,

			KeyCode.Alpha0, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4,
			KeyCode.Alpha5, KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9
		};

		static readonly HashSet<char> KeyLookup = new();
		static readonly HashSet<char> ReplayKeyLookup = new();
		static bool HasAnyInput;
		static bool ReplayOverrideActive;

		// Call from Main Thread
		public static void RefreshInputState()
		{
			lock (KeyLookup)
			{
				KeyLookup.Clear();
				HasAnyInput = false;

				if (!InputHelper.AnyKeyOrMouseHeldThisFrame) return; // early exit if no key held
				if (InputHelper.CtrlIsHeld || InputHelper.ShiftIsHeld || InputHelper.AltIsHeld) return; // don't trigger key chips if modifier is held

				foreach (KeyCode key in ValidInputKeys)
				{
					if (InputHelper.IsKeyHeld(key))
					{
						char keyChar = char.ToUpper((char)key);
						KeyLookup.Add(keyChar);
						HasAnyInput = true;
					}
				}
			}
		}

		// Call from Sim Thread
		public static bool KeyIsHeld(char key)
		{
			bool isHeld;

			lock (KeyLookup)
			{
				isHeld = ReplayOverrideActive
					? ReplayKeyLookup.Contains(char.ToUpper(key))
					: HasAnyInput && KeyLookup.Contains(key);
			}

			return isHeld;
		}

		internal static char[] CaptureHeldKeys()
		{
			lock (KeyLookup)
			{
				HashSet<char> source = ReplayOverrideActive ? ReplayKeyLookup : KeyLookup;
				if (source.Count == 0) return System.Array.Empty<char>();

				char[] result = new char[source.Count];
				source.CopyTo(result);
				System.Array.Sort(result);
				return result;
			}
		}

		internal static void SetReplayInputState(char[] heldKeys)
		{
			lock (KeyLookup)
			{
				ReplayKeyLookup.Clear();
				if (heldKeys != null)
				{
					for (int i = 0; i < heldKeys.Length; i++)
					{
						ReplayKeyLookup.Add(char.ToUpper(heldKeys[i]));
					}
				}
				ReplayOverrideActive = true;
			}
		}

		internal static void ClearReplayInputState()
		{
			lock (KeyLookup)
			{
				ReplayKeyLookup.Clear();
				ReplayOverrideActive = false;
			}
		}
	}
}