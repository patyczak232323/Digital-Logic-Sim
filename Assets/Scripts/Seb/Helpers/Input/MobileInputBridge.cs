using UnityEngine;

namespace Seb.Helpers.InputHandling
{
	public enum MobileKeyboardMode
	{
		Text,
		Code
	}

	/// <summary>
	/// Small bridge between the immediate-mode Rewired UI and the Android input layer.
	/// It deliberately contains no Android/native calls so the core UI stays platform-agnostic.
	/// </summary>
	public static class MobileInputBridge
	{
		static int lastFocusedFrame = -1000;
		static bool dismissed;
		static MobileKeyboardMode requestedMode = MobileKeyboardMode.Text;
		static Rect keyboardScreenRect;

		public static bool RuntimeEnabled { get; private set; }
		public static MobileKeyboardMode RequestedMode => requestedMode;
		public static Rect KeyboardScreenRect => keyboardScreenRect;
		public static bool KeyboardVisible => RuntimeEnabled && !dismissed && Time.frameCount - lastFocusedFrame <= 1;
		public static float TouchHitScale => RuntimeEnabled ? 1.35f : 1f;

		public static void EnableRuntime(bool enabled)
		{
			RuntimeEnabled = enabled;
			if (!enabled)
			{
				lastFocusedFrame = -1000;
				dismissed = false;
				keyboardScreenRect = default;
			}
		}

		/// <summary>Call every frame while a custom text control owns keyboard focus.</summary>
		public static void NotifyTextFocus(MobileKeyboardMode mode)
		{
			if (!RuntimeEnabled) return;
			requestedMode = mode;
			lastFocusedFrame = Time.frameCount;
		}

		/// <summary>Explicit user tap into an editor/input field re-opens the in-app keyboard.</summary>
		public static void RequestKeyboard(MobileKeyboardMode mode)
		{
			if (!RuntimeEnabled) return;
			requestedMode = mode;
			lastFocusedFrame = Time.frameCount;
			dismissed = false;
		}

		public static void DismissKeyboard()
		{
			dismissed = true;
			keyboardScreenRect = default;
		}

		public static Vector2 ExpandTouchHitSize(Vector2 screenSpaceSize)
		{
			return RuntimeEnabled ? screenSpaceSize * TouchHitScale : screenSpaceSize;
		}

		public static void SetKeyboardScreenRect(Rect rect)
		{
			keyboardScreenRect = rect;
		}

		public static bool IsPointOverKeyboard(Vector2 screenPosition)
		{
			return KeyboardVisible && keyboardScreenRect.Contains(screenPosition);
		}
	}
}
