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

		bool shift;
		bool symbols;

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

			Input.multiTouchEnabled = true;
			Input.simulateMouseWithTouches = true;
			TouchScreenKeyboard.hideInput = true;
			Screen.fullScreen = true;
			Screen.sleepTimeout = SleepTimeout.NeverSleep;
			Application.targetFrameRate = 60;
		}

		void OnDestroy()
		{
			if (instance != this) return;
			MobileInputBridge.EnableRuntime(false);
			if (ReferenceEquals(InputHelper.InputSource, inputSource))
				InputHelper.InputSource = new UnityInputSource();
			instance = null;
		}

		void Update()
		{
			if (inputSource == null) return;
			inputSource.PrepareFrame();
			UpdateLongPress();
			UpdateTwoFingerGesture();
		}

		void UpdateTwoFingerGesture()
		{
			if (Input.touchCount < 2)
			{
				twoFingerGestureActive = false;
				return;
			}

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

		void OnGUI()
		{
			if (!MobileInputBridge.KeyboardVisible || inputSource == null) return;

			Rect safe = Screen.safeArea;
			bool code = MobileInputBridge.RequestedMode == MobileKeyboardMode.Code;
			float keyboardHeight = Mathf.Clamp(safe.height * (code ? 0.50f : 0.44f), 260f, 580f);
			Rect screenRect = new(safe.xMin, safe.yMin, safe.width, keyboardHeight);
			MobileInputBridge.SetKeyboardScreenRect(screenRect);

			// OnGUI uses a top-left origin; touch/screen coordinates use a bottom-left origin.
			Rect guiRect = new(screenRect.x, Screen.height - screenRect.yMax, screenRect.width, screenRect.height);
			DrawKeyboard(guiRect, code);
		}

		void DrawKeyboard(Rect rect, bool code)
		{
			float margin = Mathf.Max(5f, rect.width * 0.004f);
			float gap = Mathf.Max(3f, rect.width * 0.0025f);
			int rows = code ? 6 : 5;
			float rowH = (rect.height - margin * 2 - gap * (rows - 1)) / rows;

			int oldButtonSize = GUI.skin.button.fontSize;
			int oldBoxSize = GUI.skin.box.fontSize;
			GUI.skin.button.fontSize = Mathf.Clamp(Mathf.RoundToInt(rowH * 0.34f), 14, 32);
			GUI.skin.box.fontSize = GUI.skin.button.fontSize;
			GUI.Box(rect, string.Empty);

			float y = rect.y + margin;
			if (code)
			{
				DrawActionRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "ESC", "<", ">", "TAB", "ENTER", "UNDO", "REDO", "PASTE", "HIDE" });
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "{", "}", "(", ")", "[", "]", ":", "=", ",", ".", "#", "_" });
				y += rowH + gap;
			}
			else
			{
				DrawActionRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "<", ">", "ALL", "COPY", "PASTE", "HIDE" });
				y += rowH + gap;
			}

			if (symbols)
			{
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "+" });
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "!", "@", "#", "$", "%", "^", "&", "*", "/", "\\", "<", ">" });
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "(", ")", "[", "]", "{", "}", ":", ";", "=", "'", "\"", "?" });
				y += rowH + gap;
			}
			else
			{
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" });
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l" });
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH),
					new[] { "z", "x", "c", "v", "b", "n", "m", "-", "_" });
				y += rowH + gap;
			}

			DrawBottomRow(new Rect(rect.x + margin, y, rect.width - margin * 2, rowH));
			GUI.skin.button.fontSize = oldButtonSize;
			GUI.skin.box.fontSize = oldBoxSize;
		}

		void DrawTextRow(Rect row, string[] keys)
		{
			float gap = Mathf.Max(2f, row.width * 0.002f);
			float keyW = (row.width - gap * (keys.Length - 1)) / keys.Length;
			for (int i = 0; i < keys.Length; i++)
			{
				Rect keyRect = new(row.x + i * (keyW + gap), row.y, keyW, row.height);
				string label = shift && keys[i].Length == 1 && char.IsLetter(keys[i][0]) ? keys[i].ToUpperInvariant() : keys[i];
				if (GUI.Button(keyRect, label))
				{
					inputSource.ScheduleText(label);
					if (shift) shift = false;
				}
			}
		}

		void DrawActionRow(Rect row, string[] keys)
		{
			float gap = Mathf.Max(2f, row.width * 0.002f);
			float keyW = (row.width - gap * (keys.Length - 1)) / keys.Length;
			for (int i = 0; i < keys.Length; i++)
			{
				Rect keyRect = new(row.x + i * (keyW + gap), row.y, keyW, row.height);
				bool repeat = keys[i] is "<" or ">";
				bool pressed = repeat ? GUI.RepeatButton(keyRect, keys[i]) : GUI.Button(keyRect, keys[i]);
				if (!pressed) continue;

				switch (keys[i])
				{
					case "ESC": inputSource.ScheduleKey(KeyCode.Escape); break;
					case "<": inputSource.ScheduleKey(KeyCode.LeftArrow); break;
					case ">": inputSource.ScheduleKey(KeyCode.RightArrow); break;
					case "TAB": inputSource.ScheduleKey(KeyCode.Tab); break;
					case "ENTER": inputSource.ScheduleKey(KeyCode.Return); break;
					case "UNDO": inputSource.ScheduleShortcut(KeyCode.Z); break;
					case "REDO": inputSource.ScheduleShortcut(KeyCode.Y); break;
					case "ALL": inputSource.ScheduleShortcut(KeyCode.A); break;
					case "COPY": inputSource.ScheduleShortcut(KeyCode.C); break;
					case "PASTE": inputSource.ScheduleShortcut(KeyCode.V); break;
					case "HIDE": MobileInputBridge.DismissKeyboard(); break;
				}
			}
		}

		void DrawBottomRow(Rect row)
		{
			float gap = Mathf.Max(2f, row.width * 0.002f);
			float unit = (row.width - gap * 4) / 10f;
			Rect shiftRect = new(row.x, row.y, unit * 1.6f, row.height);
			Rect modeRect = new(shiftRect.xMax + gap, row.y, unit * 1.3f, row.height);
			Rect spaceRect = new(modeRect.xMax + gap, row.y, unit * 4.1f, row.height);
			Rect enterRect = new(spaceRect.xMax + gap, row.y, unit * 1.3f, row.height);
			Rect backRect = new(enterRect.xMax + gap, row.y, row.xMax - (enterRect.xMax + gap), row.height);

			if (GUI.Button(shiftRect, shift ? "SHIFT*" : "SHIFT")) shift = !shift;
			if (GUI.Button(modeRect, symbols ? "ABC" : "123")) symbols = !symbols;
			if (GUI.Button(spaceRect, "SPACE")) inputSource.ScheduleText(" ");
			if (GUI.Button(enterRect, "ENTER")) inputSource.ScheduleKey(KeyCode.Return);
			if (GUI.RepeatButton(backRect, "BACK")) inputSource.ScheduleKey(KeyCode.Backspace);
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
			hardware.AnyKeyOrMouseDownThisFrame ||
			HasVirtualKeyDownThisFrame();

		public bool AnyKeyOrMouseHeldThisFrame =>
			IsMouseHeld(MouseButton.Left) ||
			hardware.AnyKeyOrMouseHeldThisFrame ||
			virtualCtrlFrame == Time.frameCount ||
			virtualShiftFrame == Time.frameCount ||
			HasVirtualKeyDownThisFrame();

		public string InputString
		{
			get
			{
				string native = hardware.InputString ?? string.Empty;
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

		public void CancelPrimaryPointerThisFrame()
		{
			cancelPrimaryFrame = Time.frameCount;
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
			return hardware.IsKeyDownThisFrame(key) ||
				(virtualDownFrame.TryGetValue(key, out int frame) && frame == Time.frameCount);
		}

		public bool IsKeyUpThisFrame(KeyCode key)
		{
			return hardware.IsKeyUpThisFrame(key) ||
				(virtualDownFrame.TryGetValue(key, out int frame) && frame == Time.frameCount);
		}

		public bool IsKeyHeld(KeyCode key)
		{
			if (hardware.IsKeyHeld(key)) return true;
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
	}
}
