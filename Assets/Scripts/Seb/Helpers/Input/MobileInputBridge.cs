using UnityEngine;

namespace Seb.Helpers.InputHandling
{
	public enum MobileKeyboardMode
	{
		Text,
		Code
	}

	/// <summary>
	/// Bridge between Rewired's immediate-mode text controls and Android's native
	/// TouchScreenKeyboard. Platform calls remain in MobileRuntime.
	/// </summary>
	public static class MobileInputBridge
	{
		static int lastFocusedFrame = -1000;
		static int suppressNativeTextThroughFrame = -1;
		static bool dismissed;
		static bool systemKeyboardActive;
		static object keyboardOwner;
		static MobileKeyboardMode requestedMode = MobileKeyboardMode.Text;
		static Rect keyboardScreenRect;

		static string requestedText = string.Empty;
		static int requestedSelectionStart;
		static int requestedSelectionLength;
		static int openRequestVersion;
		static int synchronizeRequestVersion;

		static string nativeText = string.Empty;
		static int nativeSelectionStart;
		static int nativeSelectionLength;
		static int nativeStateVersion;

		public static bool RuntimeEnabled { get; private set; }
		public static MobileKeyboardMode RequestedMode => requestedMode;
		public static Rect KeyboardScreenRect => keyboardScreenRect;
		public static bool KeyboardVisible => RuntimeEnabled && systemKeyboardActive && !dismissed;
		public static bool ConsumesNativeTextInput => KeyboardVisible || Time.frameCount <= suppressNativeTextThroughFrame;
		public static float TouchHitScale => RuntimeEnabled ? 1.35f : 1f;

		public static int OpenRequestVersion => openRequestVersion;
		public static int SynchronizeRequestVersion => synchronizeRequestVersion;
		public static string RequestedText => requestedText;
		public static int RequestedSelectionStart => requestedSelectionStart;
		public static int RequestedSelectionLength => requestedSelectionLength;

		public static void EnableRuntime(bool enabled)
		{
			RuntimeEnabled = enabled;
			if (enabled) return;

			lastFocusedFrame = -1000;
			suppressNativeTextThroughFrame = -1;
			dismissed = false;
			systemKeyboardActive = false;
			keyboardOwner = null;
			keyboardScreenRect = default;
		}

		/// <summary>Call every frame while a text control owns native keyboard focus.</summary>
		public static void NotifyTextFocus(object owner, MobileKeyboardMode mode)
		{
			if (!RuntimeEnabled || !ReferenceEquals(owner, keyboardOwner)) return;
			requestedMode = mode;
			lastFocusedFrame = Time.frameCount;
		}

		/// <summary>Open the Android keyboard or move its caret for the active control.</summary>
		public static void RequestKeyboard(
			object owner,
			MobileKeyboardMode mode,
			string text,
			int selectionStart,
			int selectionLength)
		{
			if (!RuntimeEnabled || owner == null) return;

			bool newSession = dismissed || !systemKeyboardActive || !ReferenceEquals(owner, keyboardOwner) || requestedMode != mode;
			keyboardOwner = owner;
			requestedMode = mode;
			lastFocusedFrame = Time.frameCount;
			dismissed = false;
			SetRequestedState(text, selectionStart, selectionLength);
			PublishNativeState(requestedText, requestedSelectionStart, requestedSelectionLength);

			if (newSession) openRequestVersion++;
			else synchronizeRequestVersion++;
		}

		public static bool TryConsumeKeyboardState(
			object owner,
			ref int consumedVersion,
			out string text,
			out int selectionStart,
			out int selectionLength)
		{
			text = string.Empty;
			selectionStart = 0;
			selectionLength = 0;
			if (!ReferenceEquals(owner, keyboardOwner) || consumedVersion == nativeStateVersion) return false;

			consumedVersion = nativeStateVersion;
			text = nativeText;
			selectionStart = nativeSelectionStart;
			selectionLength = nativeSelectionLength;
			return true;
		}

		/// <summary>Correct native text after a control rejects or normalizes an edit.</summary>
		public static void SynchronizeKeyboardState(
			object owner,
			string text,
			int selectionStart,
			int selectionLength)
		{
			if (!KeyboardVisible || !ReferenceEquals(owner, keyboardOwner)) return;
			SetRequestedState(text, selectionStart, selectionLength);
			synchronizeRequestVersion++;
		}

		public static void DismissKeyboard()
		{
			dismissed = true;
			suppressNativeTextThroughFrame = Time.frameCount;
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

		public static bool ShouldKeepSystemKeyboardOpen()
		{
			return RuntimeEnabled && !dismissed && Time.frameCount - lastFocusedFrame <= 1;
		}

		public static void SetSystemKeyboardActive(bool active)
		{
			systemKeyboardActive = active;
			if (!active) keyboardScreenRect = default;
		}

		public static void PublishNativeState(string text, int selectionStart, int selectionLength)
		{
			nativeText = text ?? string.Empty;
			nativeSelectionStart = Mathf.Clamp(selectionStart, 0, nativeText.Length);
			nativeSelectionLength = Mathf.Clamp(selectionLength, 0, nativeText.Length - nativeSelectionStart);
			nativeStateVersion++;
		}

		public static void NotifySystemKeyboardDismissed()
		{
			dismissed = true;
			suppressNativeTextThroughFrame = Time.frameCount;
			SetSystemKeyboardActive(false);
		}

		static void SetRequestedState(string text, int selectionStart, int selectionLength)
		{
			requestedText = text ?? string.Empty;
			requestedSelectionStart = Mathf.Clamp(selectionStart, 0, requestedText.Length);
			requestedSelectionLength = Mathf.Clamp(selectionLength, 0, requestedText.Length - requestedSelectionStart);
		}
	}
}
