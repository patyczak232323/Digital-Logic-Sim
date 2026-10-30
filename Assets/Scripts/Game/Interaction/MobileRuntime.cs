using System;
using System.Collections.Generic;
using DLS.Graphics;
using Seb.Helpers;
using Seb.Helpers.InputHandling;
using UnityEngine;

namespace DLS.Game
{
	[DefaultExecutionOrder(-5000)]
	public sealed class MobileRuntime : MonoBehaviour
	{
		const float LongPressSeconds = 0.55f;
		const float MinPinchDistance = 12f;

		static MobileRuntime instance;
		MobileInputSource inputSource;
		bool twoFingerGestureActive;
		Vector2 previousGestureCentre;
		float previousGestureDistance;

		bool longPressTracking;
		bool longPressTriggered;
		int longPressFingerId = -1;
		Vector2 longPressStartPos;
		float longPressStartTime;

		TouchScreenKeyboard systemKeyboard;
		int handledKeyboardOpenVersion;
		int handledKeyboardSynchronizeVersion;
		string lastSystemKeyboardText = string.Empty;
		int lastSystemSelectionStart;
		int lastSystemSelectionLength;

		static bool IsAndroid => Application.platform == RuntimePlatform.Android;

		// Runs on Android, and in a desktop build started with "-mobile-ui", where the mouse
		// acts as one finger so the touch interface behaves as it does on a phone.
		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
		static void Bootstrap()
		{
			if (instance != null || Application.isEditor) return;
			bool preview = Array.Exists(Environment.GetCommandLineArgs(), a => string.Equals(a, "-mobile-ui", StringComparison.OrdinalIgnoreCase));
			if (!IsAndroid && !preview) return;

			GameObject go = new("Rewired Mobile Runtime");
			DontDestroyOnLoad(go);
			instance = go.AddComponent<MobileRuntime>();
		}

		void Awake()
		{
			if (instance != null && instance != this)
			{
				Destroy(gameObject);
				return;
			}

			instance = this;
			TouchInput.EmulateWithMouse = !IsAndroid;
			inputSource = new MobileInputSource();
			InputHelper.InputSource = inputSource;
			MobileInputBridge.EnableRuntime(true);
			handledKeyboardOpenVersion = MobileInputBridge.OpenRequestVersion;
			handledKeyboardSynchronizeVersion = MobileInputBridge.SynchronizeRequestVersion;

			if (IsAndroid)
			{
				Input.multiTouchEnabled = true;
				Input.simulateMouseWithTouches = false;
				TouchScreenKeyboard.hideInput = false;
				// Which ways the screen turns is a setting (MobileUI.ApplyRuntimePreferences below).
				Screen.fullScreen = true;
				Screen.sleepTimeout = SleepTimeout.NeverSleep;
			}
			MobileUI.ApplyRuntimePreferences();
		}

		public static bool IsRunning => instance != null;

		public static void ScheduleVirtualKey(KeyCode key)
		{
			instance?.inputSource?.ScheduleKey(key);
		}

		public static void ScheduleVirtualText(string text)
		{
			instance?.inputSource?.ScheduleText(text);
		}

		public static void ScheduleVirtualShortcut(KeyCode key)
		{
			instance?.inputSource?.ScheduleShortcut(key);
		}

		/// <summary>
		/// World/menu input is suppressed while the pointer is over the keyboard.
		/// UIDrawer calls this only after the underlying mobile screen has already
		/// processed input, so the keyboard itself can receive the same touch.
		/// </summary>
		public static void RestorePrimaryPointerForKeyboardUI()
		{
			instance?.inputSource?.RestorePrimaryPointerThisFrame();
		}

		void OnDestroy()
		{
			if (instance != this) return;
			CloseSystemKeyboard(false);
			MobileInputBridge.EnableRuntime(false);
			if (ReferenceEquals(InputHelper.InputSource, inputSource))
				InputHelper.InputSource = new UnityInputSource();
			instance = null;
		}

		void Update()
		{
			if (inputSource == null) return;
			inputSource.PrepareFrame();
			UpdateSystemKeyboard();
			RefreshKeyboardScreenRect();

			// Main.Update processes world interaction before drawing UI. Pre-mark the
			// persistent mobile chrome so a tap on the top bar or the parts panel cannot leak
			// through and select/place something in the circuit underneath it.
			for (int i = 0; i < TouchInput.Count; i++)
			{
				Vector2 touchPosition = TouchInput.GetTouch(i).position;
				bool overKeyboard = MobileInputBridge.IsPointOverKeyboard(touchPosition);
				if (MobileUI.IsScreenPointOverPersistentChrome(touchPosition) || overKeyboard)
				{
					InteractionState.MouseIsOverUI = true;
					if (overKeyboard) inputSource.CancelPrimaryPointerThisFrame();
				}
			}

			// Android Back first closes the system keyboard, then the current mobile
			// screen/drawer. Only an unhandled Back is allowed to reach normal shortcuts.
			if (Input.GetKeyDown(KeyCode.Escape))
			{
				if (MobileInputBridge.KeyboardVisible)
				{
					MobileInputBridge.DismissKeyboard();
					inputSource.SuppressEscapeThisFrame();
				}
				else if (MobileUI.HandleBackButton())
				{
					inputSource.SuppressEscapeThisFrame();
				}
			}

			UpdateLongPress();
			UpdateTwoFingerGesture();
		}


		void UpdateTwoFingerGesture()
		{
			if (TouchInput.Count < 2)
			{
				twoFingerGestureActive = false;
				return;
			}

			Touch a = TouchInput.GetTouch(0);
			Touch b = TouchInput.GetTouch(1);
			if (MobileInputBridge.IsPointOverKeyboard(a.position) || MobileInputBridge.IsPointOverKeyboard(b.position))
			{
				twoFingerGestureActive = false;
				return;
			}

			Vector2 centre = (a.position + b.position) * 0.5f;
			float distance = Vector2.Distance(a.position, b.position);

			if (!twoFingerGestureActive)
			{
				twoFingerGestureActive = true;
				previousGestureCentre = centre;
				previousGestureDistance = Mathf.Max(distance, MinPinchDistance);
				inputSource.CancelPrimaryPointerThisFrame();
				return;
			}

			if (InteractionState.MouseIsOverUI)
			{
				previousGestureCentre = centre;
				previousGestureDistance = Mathf.Max(distance, MinPinchDistance);
				return;
			}

			float currentDistance = Mathf.Max(distance, MinPinchDistance);
			CameraController.ApplyMobilePanZoom(previousGestureCentre, centre, previousGestureDistance, currentDistance);
			previousGestureCentre = centre;
			previousGestureDistance = currentDistance;
		}

		void UpdateLongPress()
		{
			if (TouchInput.Count != 1)
			{
				CancelLongPress();
				return;
			}

			Touch touch = TouchInput.GetTouch(0);
			float dpi = Screen.dpi > 0 ? Screen.dpi : 320f;
			float moveTolerance = Mathf.Clamp(dpi * 0.08f, 20f, 54f);

			if (touch.phase == TouchPhase.Began)
			{
				longPressTracking = true;
				longPressTriggered = false;
				longPressFingerId = touch.fingerId;
				longPressStartPos = touch.position;
				longPressStartTime = Time.unscaledTime;
				return;
			}

			if (!longPressTracking || touch.fingerId != longPressFingerId) return;

			if (touch.phase is TouchPhase.Ended or TouchPhase.Canceled ||
				Vector2.Distance(longPressStartPos, touch.position) > moveTolerance)
			{
				CancelLongPress();
				return;
			}

			if (!longPressTriggered && Time.unscaledTime - longPressStartTime >= LongPressSeconds)
			{
				// Whether the finger is over the interface is only known once a frame has been
				// drawn with it there, so it is checked now rather than when the finger landed.
				if (MobileInputBridge.IsPointOverKeyboard(touch.position) || InteractionState.MouseIsOverUI)
				{
					CancelLongPress();
					return;
				}

				longPressTriggered = true;
				inputSource.BlockPrimaryPointerUntilRelease();
				inputSource.TriggerRightClickThisFrame(touch.position);
			}
		}

		void CancelLongPress()
		{
			longPressTracking = false;
			longPressTriggered = false;
			longPressFingerId = -1;
		}

		void RefreshKeyboardScreenRect()
		{
			if (!MobileInputBridge.KeyboardVisible)
			{
				MobileInputBridge.SetKeyboardScreenRect(default);
				return;
			}

			Rect nativeArea = TouchScreenKeyboard.area;
			MobileInputBridge.SetKeyboardScreenRect(nativeArea.height > 0 ? nativeArea : CalculateKeyboardScreenRect());
		}

		Rect CalculateKeyboardScreenRect()
		{
			Rect safe = Screen.safeArea;
			bool code = MobileInputBridge.RequestedMode == MobileKeyboardMode.Code;
			float keyboardHeight = Mathf.Clamp(
				safe.height * (code ? 0.48f : 0.42f),
				220f,
				safe.height * 0.65f);
			keyboardHeight = Mathf.Min(keyboardHeight, safe.height);
			return new Rect(safe.xMin, safe.yMin, safe.width, keyboardHeight);
		}

		void UpdateSystemKeyboard()
		{
			if (handledKeyboardOpenVersion != MobileInputBridge.OpenRequestVersion)
			{
				handledKeyboardOpenVersion = MobileInputBridge.OpenRequestVersion;
				OpenSystemKeyboard();
			}

			if (systemKeyboard == null) return;

			if (!MobileInputBridge.ShouldKeepSystemKeyboardOpen())
			{
				CloseSystemKeyboard(false);
				return;
			}

			if (handledKeyboardSynchronizeVersion != MobileInputBridge.SynchronizeRequestVersion)
			{
				handledKeyboardSynchronizeVersion = MobileInputBridge.SynchronizeRequestVersion;
				SetSystemKeyboardState(
					MobileInputBridge.RequestedText,
					MobileInputBridge.RequestedSelectionStart,
					MobileInputBridge.RequestedSelectionLength);
			}

			TouchScreenKeyboard.Status status = systemKeyboard.status;
			if (status is TouchScreenKeyboard.Status.Done or TouchScreenKeyboard.Status.Canceled or TouchScreenKeyboard.Status.LostFocus)
			{
				PublishSystemKeyboardState();
				CloseSystemKeyboard(true);
				return;
			}

			PublishSystemKeyboardState();
		}

		void OpenSystemKeyboard()
		{
			CloseSystemKeyboard(false);
			// The desktop preview types with the real keyboard.
			if (!TouchScreenKeyboard.isSupported) return;
			bool multiline = MobileInputBridge.RequestedMode == MobileKeyboardMode.Code;
			systemKeyboard = TouchScreenKeyboard.Open(
				MobileInputBridge.RequestedText,
				TouchScreenKeyboardType.Default,
				false,
				multiline,
				false,
				false,
				string.Empty);

			lastSystemKeyboardText = MobileInputBridge.RequestedText;
			lastSystemSelectionStart = MobileInputBridge.RequestedSelectionStart;
			lastSystemSelectionLength = MobileInputBridge.RequestedSelectionLength;
			handledKeyboardSynchronizeVersion = MobileInputBridge.SynchronizeRequestVersion;
			MobileInputBridge.SetSystemKeyboardActive(systemKeyboard != null);
			SetSystemKeyboardSelection(lastSystemSelectionStart, lastSystemSelectionLength);
		}

		void PublishSystemKeyboardState()
		{
			if (systemKeyboard == null) return;

			string currentText = systemKeyboard.text ?? string.Empty;
			int selectionStart = currentText.Length;
			int selectionLength = 0;
			try
			{
				if (systemKeyboard.canGetSelection)
				{
					RangeInt selection = systemKeyboard.selection;
					selectionStart = Mathf.Clamp(selection.start, 0, currentText.Length);
					selectionLength = Mathf.Clamp(selection.length, 0, currentText.Length - selectionStart);
				}
			}
			catch
			{
				// Some Android keyboards do not expose selection. Text input still works.
			}

			if (currentText == lastSystemKeyboardText &&
			    selectionStart == lastSystemSelectionStart &&
			    selectionLength == lastSystemSelectionLength)
			{
				return;
			}

			lastSystemKeyboardText = currentText;
			lastSystemSelectionStart = selectionStart;
			lastSystemSelectionLength = selectionLength;
			MobileInputBridge.PublishNativeState(currentText, selectionStart, selectionLength);
		}

		void SetSystemKeyboardState(string text, int selectionStart, int selectionLength)
		{
			if (systemKeyboard == null) return;
			text ??= string.Empty;
			if (systemKeyboard.text != text) systemKeyboard.text = text;
			lastSystemKeyboardText = text;
			lastSystemSelectionStart = Mathf.Clamp(selectionStart, 0, text.Length);
			lastSystemSelectionLength = Mathf.Clamp(selectionLength, 0, text.Length - lastSystemSelectionStart);
			SetSystemKeyboardSelection(lastSystemSelectionStart, lastSystemSelectionLength);
		}

		void SetSystemKeyboardSelection(int selectionStart, int selectionLength)
		{
			if (systemKeyboard == null) return;
			try
			{
				if (systemKeyboard.canSetSelection)
					systemKeyboard.selection = new RangeInt(selectionStart, selectionLength);
			}
			catch
			{
				// Selection support varies between Android keyboard implementations.
			}
		}

		void CloseSystemKeyboard(bool dismissedByKeyboard)
		{
			if (systemKeyboard != null)
			{
				systemKeyboard.active = false;
				systemKeyboard = null;
			}

			if (dismissedByKeyboard) MobileInputBridge.NotifySystemKeyboardDismissed();
			else MobileInputBridge.SetSystemKeyboardActive(false);
		}


	}

	public sealed class MobileInputSource : IInputSource
	{
		readonly IInputSource hardware = new UnityInputSource();
		readonly Dictionary<KeyCode, int> virtualDownFrame = new();
		int virtualCtrlFrame = -1;
		int virtualShiftFrame = -1;
		int virtualTextFrame = -1;
		string virtualText = string.Empty;
		int cancelPrimaryFrame = -1;
		bool primaryBlockedUntilRelease;
		int rightClickFrame = -1;
		int suppressEscapeFrame = -1;
		Vector2 rightClickPosition;
		Vector2 lastPointerPosition;

		// The press of a finger is reported one frame after it lands. The world and the UI
		// find what is under the pointer while they are drawn, so on the frame a finger lands
		// they still describe the previous touch; one frame later a tap on a pin or a chip is
		// seen as one. The release always follows the press by at least one frame.
		bool pressActive;
		int pressFrame = -1;
		int releaseFrame = -1;
		int trackedFingerId = -1;

		public Vector2 MousePosition
		{
			get
			{
				if (rightClickFrame == Time.frameCount) return rightClickPosition;
				if (TouchInput.Count > 0) return TouchInput.GetTouch(0).position;
				return lastPointerPosition;
			}
		}

		public bool AnyKeyOrMouseDownThisFrame =>
			IsMouseDownThisFrame(MouseButton.Left) ||
			IsMouseDownThisFrame(MouseButton.Right) ||
			(TouchInput.Count == 0 && hardware.AnyKeyOrMouseDownThisFrame) ||
			HasVirtualKeyDownThisFrame();

		public bool AnyKeyOrMouseHeldThisFrame =>
			IsMouseHeld(MouseButton.Left) ||
			(TouchInput.Count == 0 && hardware.AnyKeyOrMouseHeldThisFrame) ||
			virtualCtrlFrame == Time.frameCount ||
			virtualShiftFrame == Time.frameCount ||
			HasVirtualKeyDownThisFrame();

		public string InputString
		{
			get
			{
				string native = MobileInputBridge.ConsumesNativeTextInput ? string.Empty : hardware.InputString ?? string.Empty;
				string injected = virtualTextFrame == Time.frameCount ? virtualText : string.Empty;
				return native + injected;
			}
		}

		public Vector2 MouseScrollDelta => hardware.MouseScrollDelta;

		public void PrepareFrame()
		{
			int frame = Time.frameCount;
			int touchCount = TouchInput.Count;
			if (touchCount > 0) lastPointerPosition = TouchInput.GetTouch(0).position;

			// After a pinch or a long press the finger still down is not a new click: it
			// must not drag, select or drop anything until every finger has lifted.
			if (touchCount == 0) primaryBlockedUntilRelease = false;
			else if (touchCount >= 2) primaryBlockedUntilRelease = true;

			// A press cancelled on an earlier frame is over; the finger must lift first.
			if (pressActive && cancelPrimaryFrame >= 0 && cancelPrimaryFrame < frame && cancelPrimaryFrame >= pressFrame - 1) pressActive = false;
			if (pressActive && releaseFrame >= 0 && frame > releaseFrame) pressActive = false;

			if (touchCount == 1)
			{
				Touch touch = TouchInput.GetTouch(0);
				if (touch.phase == TouchPhase.Began)
				{
					pressActive = true;
					pressFrame = frame + 1;
					releaseFrame = -1;
					trackedFingerId = touch.fingerId;
				}
				else if (pressActive && releaseFrame < 0 && touch.fingerId == trackedFingerId && touch.phase is TouchPhase.Ended or TouchPhase.Canceled)
				{
					releaseFrame = Mathf.Max(frame, pressFrame + 1);
				}
			}
			else if (touchCount == 0 && pressActive && releaseFrame < 0)
			{
				releaseFrame = Mathf.Max(frame, pressFrame + 1);
			}

			if (virtualTextFrame < Time.frameCount)
			{
				virtualText = string.Empty;
			}
		}

		public void SuppressEscapeThisFrame()
		{
			suppressEscapeFrame = Time.frameCount;
		}

		public void CancelPrimaryPointerThisFrame()
		{
			cancelPrimaryFrame = Time.frameCount;
		}

		public void BlockPrimaryPointerUntilRelease()
		{
			cancelPrimaryFrame = Time.frameCount;
			primaryBlockedUntilRelease = true;
		}

		public void RestorePrimaryPointerThisFrame()
		{
			if (cancelPrimaryFrame == Time.frameCount) cancelPrimaryFrame = -1;
		}

		public void TriggerRightClickThisFrame(Vector2 position)
		{
			rightClickPosition = position;
			rightClickFrame = Time.frameCount;
		}

		public void ScheduleText(string text)
		{
			if (string.IsNullOrEmpty(text)) return;
			int frame = Time.frameCount + 1;
			if (virtualTextFrame != frame)
			{
				virtualTextFrame = frame;
				virtualText = text;
			}
			else
			{
				virtualText += text;
			}
		}

		public void ScheduleKey(KeyCode key)
		{
			virtualDownFrame[key] = Time.frameCount + 1;
		}

		public void ScheduleShortcut(KeyCode key)
		{
			int frame = Time.frameCount + 1;
			virtualCtrlFrame = frame;
			virtualDownFrame[key] = frame;
		}

		public bool IsKeyDownThisFrame(KeyCode key)
		{
			bool suppressTextEditingKey = MobileInputBridge.ConsumesNativeTextInput && IsNativeTextEditingKey(key);
			bool hardwareDown = !suppressTextEditingKey &&
			                    !(key == KeyCode.Escape && suppressEscapeFrame == Time.frameCount) &&
			                    hardware.IsKeyDownThisFrame(key);
			return hardwareDown ||
				(virtualDownFrame.TryGetValue(key, out int frame) && frame == Time.frameCount);
		}

		public bool IsKeyUpThisFrame(KeyCode key)
		{
			bool hardwareUp = !(MobileInputBridge.ConsumesNativeTextInput && IsNativeTextEditingKey(key)) &&
			                  hardware.IsKeyUpThisFrame(key);
			return hardwareUp ||
				(virtualDownFrame.TryGetValue(key, out int frame) && frame == Time.frameCount);
		}

		public bool IsKeyHeld(KeyCode key)
		{
			if (!(MobileInputBridge.ConsumesNativeTextInput && IsNativeTextEditingKey(key)) && hardware.IsKeyHeld(key)) return true;
			if (key is KeyCode.LeftControl or KeyCode.RightControl) return virtualCtrlFrame == Time.frameCount;
			if (key is KeyCode.LeftShift or KeyCode.RightShift) return virtualShiftFrame == Time.frameCount;
			return virtualDownFrame.TryGetValue(key, out int frame) && frame == Time.frameCount;
		}

		public bool IsMouseDownThisFrame(MouseButton button)
		{
			if (button == MouseButton.Right) return rightClickFrame == Time.frameCount || hardware.IsMouseDownThisFrame(button);
			if (button != MouseButton.Left) return hardware.IsMouseDownThisFrame(button);
			if (cancelPrimaryFrame == Time.frameCount || primaryBlockedUntilRelease) return false;
			return pressActive && Time.frameCount == pressFrame && !MobileInputBridge.IsPointOverKeyboard(MousePosition);
		}

		public bool IsMouseUpThisFrame(MouseButton button)
		{
			if (button == MouseButton.Right) return false;
			if (button != MouseButton.Left) return hardware.IsMouseUpThisFrame(button);
			bool pressReported = pressActive && Time.frameCount > pressFrame;
			// A cancelled press ends like a release, so whatever it started (a drag, a wire) finishes.
			if (cancelPrimaryFrame == Time.frameCount) return pressReported;
			if (primaryBlockedUntilRelease) return false;
			return pressReported && Time.frameCount == releaseFrame && !MobileInputBridge.IsPointOverKeyboard(MousePosition);
		}

		public bool IsMouseHeld(MouseButton button)
		{
			if (button == MouseButton.Right) return false;
			if (button != MouseButton.Left) return hardware.IsMouseHeld(button);
			if (cancelPrimaryFrame == Time.frameCount || primaryBlockedUntilRelease || !pressActive) return false;
			int frame = Time.frameCount;
			return frame >= pressFrame && (releaseFrame < 0 || frame < releaseFrame) && !MobileInputBridge.IsPointOverKeyboard(MousePosition);
		}

		bool HasVirtualKeyDownThisFrame()
		{
			foreach (KeyValuePair<KeyCode, int> pair in virtualDownFrame)
				if (pair.Value == Time.frameCount) return true;
			return false;
		}

		static bool IsNativeTextEditingKey(KeyCode key)
		{
			return key is KeyCode.Backspace or KeyCode.Delete or KeyCode.Return or KeyCode.KeypadEnter or
				KeyCode.LeftArrow or KeyCode.RightArrow or KeyCode.UpArrow or KeyCode.DownArrow or
				KeyCode.Home or KeyCode.End or KeyCode.PageUp or KeyCode.PageDown;
		}
	}
}
