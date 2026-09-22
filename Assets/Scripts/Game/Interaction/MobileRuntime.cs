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

		bool shift;
		bool symbols;

		GUIStyle keyboardPanelStyle;
		GUIStyle keyboardKeyStyle;
		GUIStyle keyboardActionStyle;
		GUIStyle keyboardAccentStyle;
		GUIStyle keyboardLabelStyle;
		Texture2D keyboardAccentLine;

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

			// Main.Update processes world interaction before drawing UI. Pre-mark the
			// persistent mobile chrome so a first tap on the dock/top bar cannot leak
			// through and select/place something in the circuit underneath it.
			if (Input.touchCount > 0)
			{
				Vector2 touchPosition = Input.GetTouch(0).position;
				if (MobileUI.IsScreenPointOverPersistentChrome(touchPosition) || MobileInputBridge.IsPointOverKeyboard(touchPosition))
				{
					InteractionState.MouseIsOverUI = true;
					if (MobileInputBridge.IsPointOverKeyboard(touchPosition))
						inputSource.CancelPrimaryPointerThisFrame();
				}
			}

			// Android Back first closes the custom keyboard, then the current mobile
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
			EnsureKeyboardStyles();

			float margin = Mathf.Clamp(rect.width * 0.008f, 7f, 13f);
			float gap = Mathf.Clamp(rect.width * 0.0045f, 4f, 9f);
			int rows = code ? 6 : 5;
			float rowH = (rect.height - margin * 2f - gap * (rows - 1)) / rows;

			int keyFont = Mathf.Clamp(Mathf.RoundToInt(rowH * 0.34f), 15, 31);
			int actionFont = Mathf.Clamp(Mathf.RoundToInt(rowH * 0.27f), 12, 24);
			keyboardKeyStyle.fontSize = keyFont;
			keyboardAccentStyle.fontSize = keyFont;
			keyboardActionStyle.fontSize = actionFont;
			keyboardLabelStyle.fontSize = Mathf.Clamp(Mathf.RoundToInt(rowH * 0.24f), 11, 20);

			GUI.Box(rect, GUIContent.none, keyboardPanelStyle);
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, Mathf.Max(2f, rect.height * 0.006f)), keyboardAccentLine);

			float y = rect.y + margin;
			Rect innerRow = new(rect.x + margin, y, rect.width - margin * 2f, rowH);
			DrawToolbar(innerRow, code, gap);
			y += rowH + gap;

			if (code)
			{
				DrawTextRow(
					new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "{", "}", "(", ")", "[", "]", ":", "=", ",", ".", "#", "_" },
					gap,
					0f,
					true);
				y += rowH + gap;
			}

			if (symbols)
			{
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "+" }, gap, 0f, false);
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "!", "@", "#", "$", "%", "^", "&", "*", "/", "\\", "<", ">" }, gap, 0f, false);
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "(", ")", "[", "]", "{", "}", ":", ";", "=", "'", "\"", "?" }, gap, 0f, false);
				y += rowH + gap;
			}
			else
			{
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" }, gap, 0f, false);
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l" }, gap, rect.width * 0.035f, false);
				y += rowH + gap;
				DrawTextRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH),
					new[] { "z", "x", "c", "v", "b", "n", "m", "-", "_" }, gap, rect.width * 0.065f, false);
				y += rowH + gap;
			}

			DrawBottomRow(new Rect(rect.x + margin, y, rect.width - margin * 2f, rowH), gap);
		}

		void DrawToolbar(Rect row, bool code, float gap)
		{
			float titleW = Mathf.Clamp(row.width * 0.12f, 72f, 132f);
			GUI.Label(new Rect(row.x, row.y, titleW, row.height), code ? "RHDL" : "TEXT", keyboardLabelStyle);

			string[] labels = code
				? new[] { "\u2190", "\u2192", "TAB", "UNDO", "REDO", "PASTE", "\u00D7" }
				: new[] { "\u2190", "\u2192", "ALL", "COPY", "PASTE", "\u00D7" };
			string[] actions = code
				? new[] { "LEFT", "RIGHT", "TAB", "UNDO", "REDO", "PASTE", "HIDE" }
				: new[] { "LEFT", "RIGHT", "ALL", "COPY", "PASTE", "HIDE" };

			float x = row.x + titleW + gap;
			float available = row.xMax - x;
			float keyW = (available - gap * (labels.Length - 1)) / labels.Length;
			for (int i = 0; i < labels.Length; i++)
			{
				Rect keyRect = new(x + i * (keyW + gap), row.y, keyW, row.height);
				bool repeat = actions[i] is "LEFT" or "RIGHT";
				bool pressed = repeat
					? GUI.RepeatButton(keyRect, labels[i], keyboardActionStyle)
					: GUI.Button(keyRect, labels[i], keyboardActionStyle);
				if (pressed) RunKeyboardAction(actions[i]);
			}
		}

		void DrawTextRow(Rect row, string[] keys, float gap, float sideInset, bool accessoryRow)
		{
			Rect usable = new(row.x + sideInset, row.y, row.width - sideInset * 2f, row.height);
			float keyW = (usable.width - gap * (keys.Length - 1)) / keys.Length;
			GUIStyle style = accessoryRow ? keyboardActionStyle : keyboardKeyStyle;

			for (int i = 0; i < keys.Length; i++)
			{
				Rect keyRect = new(usable.x + i * (keyW + gap), usable.y, keyW, usable.height);
				string label = shift && keys[i].Length == 1 && char.IsLetter(keys[i][0])
					? keys[i].ToUpperInvariant()
					: keys[i];

				if (!GUI.Button(keyRect, label, style)) continue;
				inputSource.ScheduleText(label);
				if (shift) shift = false;
			}
		}

		void DrawBottomRow(Rect row, float gap)
		{
			float shiftW = row.width * 0.115f;
			float modeW = row.width * 0.105f;
			float enterW = row.width * 0.115f;
			float backW = row.width * 0.13f;
			float spaceW = row.width - shiftW - modeW - enterW - backW - gap * 4f;

			Rect shiftRect = new(row.x, row.y, shiftW, row.height);
			Rect modeRect = new(shiftRect.xMax + gap, row.y, modeW, row.height);
			Rect spaceRect = new(modeRect.xMax + gap, row.y, spaceW, row.height);
			Rect enterRect = new(spaceRect.xMax + gap, row.y, enterW, row.height);
			Rect backRect = new(enterRect.xMax + gap, row.y, backW, row.height);

			if (GUI.Button(shiftRect, "\u21E7", shift ? keyboardAccentStyle : keyboardActionStyle)) shift = !shift;
			if (GUI.Button(modeRect, symbols ? "ABC" : "123", symbols ? keyboardAccentStyle : keyboardActionStyle)) symbols = !symbols;
			if (GUI.Button(spaceRect, string.Empty, keyboardKeyStyle)) inputSource.ScheduleText(" ");
			if (GUI.Button(enterRect, "\u21B5", keyboardActionStyle)) inputSource.ScheduleKey(KeyCode.Return);
			if (GUI.RepeatButton(backRect, "\u232B", keyboardActionStyle)) inputSource.ScheduleKey(KeyCode.Backspace);
		}

		void RunKeyboardAction(string action)
		{
			switch (action)
			{
				case "LEFT": inputSource.ScheduleKey(KeyCode.LeftArrow); break;
				case "RIGHT": inputSource.ScheduleKey(KeyCode.RightArrow); break;
				case "TAB": inputSource.ScheduleKey(KeyCode.Tab); break;
				case "UNDO": inputSource.ScheduleShortcut(KeyCode.Z); break;
				case "REDO": inputSource.ScheduleShortcut(KeyCode.Y); break;
				case "ALL": inputSource.ScheduleShortcut(KeyCode.A); break;
				case "COPY": inputSource.ScheduleShortcut(KeyCode.C); break;
				case "PASTE": inputSource.ScheduleShortcut(KeyCode.V); break;
				case "HIDE": MobileInputBridge.DismissKeyboard(); break;
			}
		}

		void EnsureKeyboardStyles()
		{
			if (keyboardKeyStyle != null) return;

			Texture2D panel = CreateSolidTexture(new Color(0.045f, 0.055f, 0.075f, 0.985f));
			Texture2D key = CreateRoundedTexture(new Color(0.14f, 0.17f, 0.22f, 1f));
			Texture2D keyPressed = CreateRoundedTexture(new Color(0.22f, 0.27f, 0.34f, 1f));
			Texture2D action = CreateRoundedTexture(new Color(0.095f, 0.115f, 0.15f, 1f));
			Texture2D accent = CreateRoundedTexture(RewiredUI.Accent);
			keyboardAccentLine = CreateSolidTexture(RewiredUI.Accent);

			keyboardPanelStyle = new GUIStyle(GUI.skin.box);
			keyboardPanelStyle.normal.background = panel;
			keyboardPanelStyle.border = new RectOffset(0, 0, 0, 0);

			keyboardKeyStyle = MakeKeyboardStyle(key, keyPressed, Color.white);
			keyboardActionStyle = MakeKeyboardStyle(action, keyPressed, new Color(0.84f, 0.88f, 0.94f, 1f));
			keyboardAccentStyle = MakeKeyboardStyle(accent, keyPressed, Color.white);

			keyboardLabelStyle = new GUIStyle(GUI.skin.label)
			{
				alignment = TextAnchor.MiddleLeft,
				fontStyle = FontStyle.Bold,
				clipping = TextClipping.Clip,
				padding = new RectOffset(8, 4, 0, 0)
			};
			keyboardLabelStyle.normal.textColor = new Color(0.55f, 0.62f, 0.72f, 1f);
		}

		static GUIStyle MakeKeyboardStyle(Texture2D normal, Texture2D pressed, Color text)
		{
			GUIStyle style = new(GUI.skin.button)
			{
				alignment = TextAnchor.MiddleCenter,
				fontStyle = FontStyle.Normal,
				border = new RectOffset(10, 10, 10, 10),
				padding = new RectOffset(4, 4, 2, 2),
				margin = new RectOffset(0, 0, 0, 0)
			};
			style.normal.background = normal;
			style.hover.background = normal;
			style.focused.background = normal;
			style.active.background = pressed;
			style.normal.textColor = text;
			style.hover.textColor = text;
			style.focused.textColor = text;
			style.active.textColor = Color.white;
			return style;
		}

		static Texture2D CreateSolidTexture(Color colour)
		{
			Texture2D texture = new(1, 1, TextureFormat.RGBA32, false)
			{
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
				hideFlags = HideFlags.HideAndDontSave
			};
			texture.SetPixel(0, 0, colour);
			texture.Apply(false, true);
			return texture;
		}

		static Texture2D CreateRoundedTexture(Color colour)
		{
			const int size = 32;
			const float radius = 7f;
			float half = size * 0.5f;
			Color[] pixels = new Color[size * size];

			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float px = x + 0.5f - half;
					float py = y + 0.5f - half;
					float qx = Mathf.Abs(px) - (half - radius);
					float qy = Mathf.Abs(py) - (half - radius);
					float ox = Mathf.Max(qx, 0f);
					float oy = Mathf.Max(qy, 0f);
					float distance = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;
					float alpha = Mathf.Clamp01(0.5f - distance);
					Color pixel = colour;
					pixel.a *= alpha;
					pixels[y * size + x] = pixel;
				}
			}

			Texture2D texture = new(size, size, TextureFormat.RGBA32, false)
			{
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
				hideFlags = HideFlags.HideAndDontSave
			};
			texture.SetPixels(pixels);
			texture.Apply(false, true);
			return texture;
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

		public void SuppressEscapeThisFrame()
		{
			suppressEscapeFrame = Time.frameCount;
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
			bool hardwareDown = !(key == KeyCode.Escape && suppressEscapeFrame == Time.frameCount) && hardware.IsKeyDownThisFrame(key);
			return hardwareDown ||
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
