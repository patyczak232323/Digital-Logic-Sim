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

		bool singleFingerPanActive;
		int singleFingerPanId = -1;
		Vector2 previousSinglePanPosition;

		bool edgeSwipeTracking;
		int edgeSwipeFingerId = -1;
		Vector2 edgeSwipeStart;

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

		[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
		static void Bootstrap()
		{
#if UNITY_ANDROID && !UNITY_EDITOR
			if (instance != null) return;
			GameObject go = new("Rewired Mobile Runtime");
			DontDestroyOnLoad(go);
			instance = go.AddComponent<MobileRuntime>();
#endif
		}

		void Awake()
		{
			if (instance != null && instance != this)
			{
				Destroy(gameObject);
				return;
			}

			instance = this;
			inputSource = new MobileInputSource();
			InputHelper.InputSource = inputSource;
			MobileInputBridge.EnableRuntime(true);
			handledKeyboardOpenVersion = MobileInputBridge.OpenRequestVersion;
			handledKeyboardSynchronizeVersion = MobileInputBridge.SynchronizeRequestVersion;

			Input.multiTouchEnabled = true;
			Input.simulateMouseWithTouches = false;
			TouchScreenKeyboard.hideInput = false;
			Screen.autorotateToPortrait = false;
			Screen.autorotateToPortraitUpsideDown = false;
			Screen.autorotateToLandscapeLeft = true;
			Screen.autorotateToLandscapeRight = true;
			Screen.orientation = ScreenOrientation.AutoRotation;
			Screen.fullScreen = true;
			Screen.sleepTimeout = SleepTimeout.NeverSleep;
			MobileUI.ApplyRuntimePreferences();
		}

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
			// persistent mobile chrome so a first tap on the dock/top bar cannot leak
			// through and select/place something in the circuit underneath it.
			for (int i = 0; i < Input.touchCount; i++)
			{
				Vector2 touchPosition = Input.GetTouch(i).position;
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

			UpdateEdgeSwipe();
			UpdateSingleFingerPan();
			if (!MobileUI.PanMode && !edgeSwipeTracking) UpdateLongPress();
			else CancelLongPress();
			UpdateTwoFingerGesture();
		}


		void UpdateEdgeSwipe()
		{
			if (UIDrawer.ActiveMenu != UIDrawer.MenuType.None ||
			    MobileInputBridge.KeyboardVisible ||
			    Input.touchCount != 1)
			{
				CancelEdgeSwipe();
				return;
			}

			Touch touch = Input.GetTouch(0);
			float edgeWidth = Mathf.Max(24f, Screen.width * 0.035f);
			float edgeStart = Screen.safeArea.xMin + edgeWidth;

			if (touch.phase == TouchPhase.Began)
			{
				if (touch.position.x <= edgeStart)
				{
					InteractionState.MouseIsOverUI = true;
					edgeSwipeTracking = true;
					edgeSwipeFingerId = touch.fingerId;
					edgeSwipeStart = touch.position;
				}
				return;
			}

			if (!edgeSwipeTracking || touch.fingerId != edgeSwipeFingerId) return;

			// A possible edge gesture owns the pointer from its first frame so it
			// cannot accidentally place/select a component underneath the finger.
			InteractionState.MouseIsOverUI = true;
			inputSource.CancelPrimaryPointerThisFrame();

			Vector2 delta = touch.position - edgeSwipeStart;
			float requiredDistance = Mathf.Max(80f, Screen.width * 0.12f);
			if (delta.x >= requiredDistance && Mathf.Abs(delta.x) > Mathf.Abs(delta.y) * 1.25f)
			{
				inputSource.CancelPrimaryPointerThisFrame();
				MobileUI.OpenDrawer();
				CancelEdgeSwipe();
				return;
			}

			if (touch.phase is TouchPhase.Ended or TouchPhase.Canceled)
				CancelEdgeSwipe();
		}

		void CancelEdgeSwipe()
		{
			edgeSwipeTracking = false;
			edgeSwipeFingerId = -1;
		}

		void UpdateSingleFingerPan()
		{
			if (!MobileUI.PanMode ||
			    UIDrawer.ActiveMenu != UIDrawer.MenuType.None ||
			    Input.touchCount != 1)
			{
				CancelSingleFingerPan();
				return;
			}

			Touch touch = Input.GetTouch(0);
			if (MobileInputBridge.IsPointOverKeyboard(touch.position) || InteractionState.MouseIsOverUI)
			{
				CancelSingleFingerPan();
				return;
			}

			// In PAN mode a one-finger gesture must never leak through as an editor click.
			inputSource.CancelPrimaryPointerThisFrame();

			if (touch.phase == TouchPhase.Began)
			{
				singleFingerPanActive = true;
				singleFingerPanId = touch.fingerId;
				previousSinglePanPosition = touch.position;
				return;
			}

			if (!singleFingerPanActive || touch.fingerId != singleFingerPanId) return;

			if (touch.phase == TouchPhase.Moved)
			{
				CameraController.ApplyMobilePanZoom(
					previousSinglePanPosition,
					touch.position,
					100f,
					100f);
				previousSinglePanPosition = touch.position;
			}
			else if (touch.phase is TouchPhase.Ended or TouchPhase.Canceled)
			{
				CancelSingleFingerPan();
			}
		}

		void CancelSingleFingerPan()
		{
			singleFingerPanActive = false;
			singleFingerPanId = -1;
		}

		void UpdateTwoFingerGesture()
		{
			if (Input.touchCount < 2)
			{
				twoFingerGestureActive = false;
				return;
			}

			CancelSingleFingerPan();
			Touch a = Input.GetTouch(0);
			Touch b = Input.GetTouch(1);
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
			if (Input.touchCount != 1)
			{
				CancelLongPress();
				return;
			}

			Touch touch = Input.GetTouch(0);
			if (MobileInputBridge.IsPointOverKeyboard(touch.position) || InteractionState.MouseIsOverUI)
			{
				CancelLongPress();
				return;
			}

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
				longPressTriggered = true;
				inputSource.CancelPrimaryPointerThisFrame();
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
		int rightClickFrame = -1;
		int suppressEscapeFrame = -1;
		Vector2 rightClickPosition;
		Vector2 lastPointerPosition;

		public Vector2 MousePosition
		{
			get
			{
				if (rightClickFrame == Time.frameCount) return rightClickPosition;
				if (Input.touchCount > 0) return Input.GetTouch(0).position;
				return lastPointerPosition;
			}
		}

		public bool AnyKeyOrMouseDownThisFrame =>
			IsMouseDownThisFrame(MouseButton.Left) ||
			IsMouseDownThisFrame(MouseButton.Right) ||
			(Input.touchCount == 0 && hardware.AnyKeyOrMouseDownThisFrame) ||
			HasVirtualKeyDownThisFrame();

		public bool AnyKeyOrMouseHeldThisFrame =>
			IsMouseHeld(MouseButton.Left) ||
			(Input.touchCount == 0 && hardware.AnyKeyOrMouseHeldThisFrame) ||
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
			if (Input.touchCount > 0) lastPointerPosition = Input.GetTouch(0).position;
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
			if (cancelPrimaryFrame == Time.frameCount || Input.touchCount != 1) return false;

			Touch touch = Input.GetTouch(0);
			if (MobileInputBridge.IsPointOverKeyboard(touch.position)) return false;
			return touch.phase == TouchPhase.Began;
		}

		public bool IsMouseUpThisFrame(MouseButton button)
		{
			if (button == MouseButton.Right) return false;
			if (button != MouseButton.Left) return hardware.IsMouseUpThisFrame(button);
			if (cancelPrimaryFrame == Time.frameCount) return true;
			if (Input.touchCount != 1) return false;

			Touch touch = Input.GetTouch(0);
			if (MobileInputBridge.IsPointOverKeyboard(touch.position)) return false;
			return touch.phase is TouchPhase.Ended or TouchPhase.Canceled;
		}

		public bool IsMouseHeld(MouseButton button)
		{
			if (button == MouseButton.Right) return false;
			if (button != MouseButton.Left) return hardware.IsMouseHeld(button);
			if (cancelPrimaryFrame == Time.frameCount || Input.touchCount != 1) return false;

			Touch touch = Input.GetTouch(0);
			if (MobileInputBridge.IsPointOverKeyboard(touch.position)) return false;
			return touch.phase is TouchPhase.Began or TouchPhase.Moved or TouchPhase.Stationary;
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
