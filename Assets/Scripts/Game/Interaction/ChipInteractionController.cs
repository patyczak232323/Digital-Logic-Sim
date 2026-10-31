using System.Collections.Generic;
using System.Linq;
using DLS.Description;
using DLS.Graphics;
using DLS.SaveSystem;
using Seb.Helpers;
using Seb.Types;
using UnityEngine;

namespace DLS.Game
{
	public class ChipInteractionController
	{
		public readonly Project project;

		// ---- Selection and placement state ----
		public readonly List<IMoveable> SelectedElements = new();
		public List<WireInstance> DuplicatedWires = new();
		public WireInstance WireToPlace;
		bool isPlacingNewElements;
		float itemPlacementCurrVerticalSpacing;
		bool newElementsAreDuplicatedElements;
		Vector2 moveElementMouseStartPos;
		IMoveable[] Obstacles; // Obstacles are the non-selected items when a group of elements is being moved
		public Vector2 SelectionBoxStartPos;
		StraightLineMoveState straightLineMoveState;
		bool hasExittedMultiModeSincePlacementStart;

		// ---- Wire edit state ----
		public WireInstance wireToEdit;
		public int wireEditPointIndex = -1;
		public bool wireEditCanInsertPoint;
		Vector2 wireEditPointOld;
		public int wireEditPointSelectedIndex;
		public bool isMovingWireEditPoint;

		public DevChipInstance ActiveDevChip => project.ViewedChip;
		public bool IsMovingSelection { get; private set; }
		public bool IsCreatingSelectionBox { get; private set; }
		public Vector2 SelectionBoxCentre => (SelectionBoxStartPos + InputHelper.MousePosWorld) / 2;
		public Vector2 SelectionBoxSize => Maths.Abs(SelectionBoxStartPos - InputHelper.MousePosWorld);

		public bool HasControl => !UIDrawer.InInputBlockingMenu() && project.CanEditViewedChip;

		// Cannot interact with elements when other elements are being moved, in a menu, or drawing a selection box
		bool CanInteract => !IsMovingSelection && !UIDrawer.InInputBlockingMenu() && !IsCreatingSelectionBox && !InteractionState.MouseIsOverUI;
		public bool IsCreatingWire => WireToPlace != null;
		public bool IsPlacingElements => isPlacingNewElements;
		public bool IsPlacingElementOrCreatingWire => isPlacingNewElements || IsCreatingWire;
		public bool IsPlacingOrMovingElementOrCreatingWire => isPlacingNewElements || IsMovingSelection || IsCreatingWire || isMovingWireEditPoint;
		public bool CanInteractWithPin => CanInteract;
		public bool CanInteractWithPinStateDisplay => CanInteract && !IsCreatingWire && Project.ActiveProject.CanEditViewedChip;
		public bool CanInteractWithPinHandle => CanInteractWithPinStateDisplay;


		public ChipInteractionController(Project project)
		{
			this.project = project;
		}

		public void Update()
		{
			HandleKeyboardInput();
			HandleMouseInput();
		}

		// Don't allow interaction with wire that's currently being placed (this would allow it to try to connect to itself for example...)
		public bool CanInteractWithWire(WireInstance wire) => CanInteract && wire != WireToPlace;

		public bool CanCompleteWireConnection(WireInstance wireToConnectTo, out PinInstance endPin)
		{
			// If we're joining this wire to an existing wire, choose the appropriate source/target pin from that wire to connect to
			endPin = WireToPlace.FirstPin.IsSourcePin ? wireToConnectTo.TargetPin_BusCorrected : wireToConnectTo.SourcePin;
			return CanCompleteWireConnection(endPin, wireToConnectTo);
		}

		public bool CanCompleteWireConnection(PinInstance endPin, WireInstance wireToConnectTo = null)
		{
			if (!IsCreatingWire) return false;

			PinInstance startPin = WireToPlace.FirstPin;
			bool connectingFromWire = WireToPlace.FirstConnectionInfo.IsConnectedAtWire;
			bool connectingToWire = wireToConnectTo != null;
			WireInstance wireConnection = wireToConnectTo ?? WireToPlace.FirstConnectionInfo.connectedWire;

			// Don't allow wire-to-wire connections (ambiguous where to get signal source from, and where to carry it to)
			if (connectingFromWire && connectingToWire) return false;

			// (Maybe temporary restriction?): Don't allow sourcePin-to-wire connections (unless the wire is a bus wire).
			// This is because if the two source pins have different states, then the wire would need to be coloured differently
			// from the connection point onwards (depending on which of the conflicting states is chosen)
			if (connectingFromWire || connectingToWire)
			{
				PinInstance pinConnection = connectingToWire ? startPin : endPin;
				if (pinConnection.IsSourcePin && !wireConnection.IsBusWire) return false;
			}

			// Ensure connection is between a source and a target pin
			// (note: if connection starts or ends at a wire then it's valid regardless, since we can just pick source/target pin from that wire as needed)
			bool hasSourceAndTarget = startPin.IsSourcePin != endPin.IsSourcePin || connectingFromWire || connectingToWire;
			if (!hasSourceAndTarget || endPin.bitCount != startPin.bitCount) return false;

			// Only allow connecting bus origin and terminus if they are linked together (i.e. were created together at same time; rather than any random pair)
			// Note: could consider lifting this restriction, but need to investigate impact on simulation...
			if (startPin.IsBusPin && endPin.IsBusPin)
			{
				SubChipInstance busA = (SubChipInstance)endPin.parent;
				SubChipInstance busB = (SubChipInstance)startPin.parent;
				return busA.LinkedBusPairID == busB.ID;
			}


			return true;
		}

		public static bool IsSelected(IMoveable element) => element.IsSelected;
		
		public void Delete(IMoveable element)
		{
			DeleteElements(new List<IMoveable>(new[] { element }));
		}

		void DeleteElements(List<IMoveable> elements, bool clearSelection = true)
		{
			if (!HasControl) return;
			List<IMoveable> elementsToDelete = elements.Concat(GetNonIncludedLinkedBusElements(elements)).ToList();
			ActiveDevChip.UndoController.RecordDeleteElements(elementsToDelete);

			foreach (IMoveable element in elementsToDelete)
			{
				if (element is SubChipInstance subChip) ActiveDevChip.DeleteSubChip(subChip);
				else if (element is DevPinInstance devPin) ActiveDevChip.DeleteDevPin(devPin);
			}

			if (clearSelection) SelectedElements.Clear();
		}

		void DeleteSelected()
		{
			// Delete selected subchips/pins
			if (SelectedElements.Count > 0)
			{
				DeleteElements(SelectedElements);
			}
			// Delete wire under mouse
			else if (InteractionState.ElementUnderMouse is WireInstance wire && wire != wireToEdit)
			{
				DeleteWire(wire);
			}
			// Delete wire point under mouse (in wire edit mode)
			else if (wireToEdit != null && wireEditPointIndex != -1)
			{
				bool isWireToWireConnectionPoint = wireEditPointIndex == 0 || wireEditPointIndex == wireToEdit.WirePointCount - 1;
				// Can't delete the point connecting a wire to another wire
				if (!isWireToWireConnectionPoint)
				{
					foreach (WireInstance other in ActiveDevChip.Wires)
					{
						if (other.ConnectedWire == wireToEdit)
						{
							other.NotifyParentWirePointWillBeDeleted(wireEditPointIndex);
						}
					}

					wireToEdit.DeleteWirePoint(wireEditPointIndex);
					wireEditPointIndex = -1;
					isMovingWireEditPoint = false;
				}
			}
		}

		public void DeleteWire(WireInstance wire)
		{
			if (HasControl)
			{
				ActiveDevChip.UndoController.RecordDeleteWire(wire);
				ActiveDevChip.DeleteWire(wire);
			}
		}

		public void ToggleDevPinState(DevPinInstance devPin, int bitIndex)
		{
			if (HasControl) devPin.ToggleState(bitIndex);
		}

		public void MirrorSubChips(IEnumerable<SubChipInstance> subchips, bool horizontal)
		{
			if (!HasControl || IsPlacingOrMovingElementOrCreatingWire) return;

			List<SubChipInstance> mirrorTargets = subchips
				.Where(s => s != null && !s.IsBus)
				.Distinct()
				.ToList();

			if (mirrorTargets.Count == 0) return;

			ActiveDevChip.UndoController.RecordMirrorSubChips(mirrorTargets, horizontal);
			foreach (SubChipInstance subchip in mirrorTargets)
			{
				if (horizontal) subchip.MirrorHorizontal();
				else subchip.MirrorVertical();
			}
		}

		void HandleKeyboardInput()
		{
			// Ignore shortcuts if don't have control
			if (!HasControl) return;

			if (KeyboardShortcuts.UndoShortcutTriggered) ActiveDevChip.UndoController.TryUndo();
			else if (KeyboardShortcuts.RedoShortcutTriggered) ActiveDevChip.UndoController.TryRedo();


			if (!KeyboardShortcuts.StraightLineModeHeld) straightLineMoveState = StraightLineMoveState.None;

			if (KeyboardShortcuts.SearchShortcutTriggered)
			{
				UIDrawer.SetActiveMenu(UIDrawer.MenuType.Search);
			}


			if (KeyboardShortcuts.SelectAllShortcutTriggered) SelectAll();
			if (KeyboardShortcuts.CopyShortcutTriggered) CopySelection();
			if (KeyboardShortcuts.CutShortcutTriggered) CutSelection();
			if (KeyboardShortcuts.PasteShortcutTriggered) Paste();

			if (KeyboardShortcuts.DuplicateShortcutTriggered)
			{
				if (SelectedElements.Count > 0 && !IsPlacingOrMovingElementOrCreatingWire)
				{
					DuplicateSelectedElements();
				}
			}

			if (KeyboardShortcuts.MirrorHorizontalShortcutTriggered)
			{
				MirrorSubChips(SelectedElements.OfType<SubChipInstance>(), true);
			}
			if (KeyboardShortcuts.MirrorVerticalShortcutTriggered)
			{
				MirrorSubChips(SelectedElements.OfType<SubChipInstance>(), false);
			}

			if (KeyboardShortcuts.DeleteShortcutTriggered)
			{
				if (IsCreatingWire)
				{
					if (!WireToPlace.RemoveLastPoint())
					{
						CancelEverything();
					}
				}
				else
				{
					if (isPlacingNewElements) CancelPlacingItems();
					else DeleteSelected();
				}
			}

			if (KeyboardShortcuts.ConfirmShortcutTriggered)
			{
				ExitWireEditMode();
			}

			if (KeyboardShortcuts.CancelShortcutTriggered)
			{
				CancelEverything();
			}
		}

		void HandleMouseInput()
		{
			if (HasControl) UpdatePositionsToMouse();

			// --- Mouse button input ---
			if (InputHelper.IsMouseDownThisFrame(MouseButton.Left)) HandleLeftMouseDown();
			if (InputHelper.IsMouseUpThisFrame(MouseButton.Left)) HandleLeftMouseUp();
			if (InputHelper.IsMouseDownThisFrame(MouseButton.Right)) HandleRightMouseDown();

			// Shift + scroll to increase vertical spacing between elements when placing multiple at a time
			// (disabled if elements were duplicated since then we want to preserve relative positions)
			if (isPlacingNewElements && !newElementsAreDuplicatedElements && InputHelper.ShiftIsHeld)
			{
				itemPlacementCurrVerticalSpacing += InputHelper.MouseScrollDelta.y * DrawSettings.GridSize;
				itemPlacementCurrVerticalSpacing = Mathf.Max(0, itemPlacementCurrVerticalSpacing);
			}
		}

		List<IMoveable> GetNonIncludedLinkedBusElements(List<IMoveable> elements)
		{
			List<IMoveable> nonIncludedBusPairs = new();
			HashSet<int> elementIDs = elements.Select(e => e.ID).ToHashSet();

			foreach (IMoveable element in elements)
			{
				if (element is SubChipInstance subChip && subChip.IsBus)
				{
					if (!elementIDs.Contains(subChip.LinkedBusPairID))
					{
						ActiveDevChip.TryGetSubChipByID(subChip.LinkedBusPairID, out SubChipInstance pairedBus);
						nonIncludedBusPairs.Add(pairedBus);
					}
				}
			}

			return nonIncludedBusPairs;
		}

		// Set the correct LinkedBusPairIDs on duplicated elements (still set to original IDs at this point)
		// busPairOfOriginal maps the original ID of each copied bus terminus to the original ID of its origin.
		static void LinkDuplicatedBuses(List<IMoveable> duplicatedElements, Dictionary<int, int> busPairOfOriginal)
		{
			List<SubChipInstance> busOrigins = new();
			List<SubChipInstance> busTerminuses = new();

			foreach (IMoveable element in duplicatedElements)
			{
				if (element is not SubChipInstance subChip) continue;
				if (ChipTypeHelper.IsBusTerminusType(subChip.ChipType)) busTerminuses.Add(subChip);
				else if (ChipTypeHelper.IsBusOriginType(subChip.ChipType)) busOrigins.Add(subChip);
			}

			foreach (SubChipInstance busOrigin in busOrigins)
			{
				// A copied origin still holds the original ID of its terminus
				if (!busPairOfOriginal.TryGetValue(busOrigin.LinkedBusPairID, out int originalBusOriginId)) continue;
				foreach (SubChipInstance busTerminus in busTerminuses)
				{
					if (busTerminus.LinkedBusPairID == originalBusOriginId)
					{
						busOrigin.SetLinkedBusPair(busTerminus);
						busTerminus.SetLinkedBusPair(busOrigin);
						break;
					}
				}
			}
		}

		// ---- Copy and paste ----

		// A copy of some elements and the wires between them, independent of the chip they came from,
		// so it can be pasted after the originals are deleted, or into another chip.
		sealed class ElementSnapshot
		{
			public int OriginalID;
			public Vector2 Position;
			public Vector2 AnchorPosition; // the point that follows the mouse when this element is nearest to it
			public bool IsDevPin;
			public bool IsInputPin;
			public PinDescription PinDesc;
			public ChipDescription ChipDesc;
			public SubChipDescription SubChipDesc;
		}

		sealed class WireSnapshot
		{
			public PinAddress Source;
			public PinAddress Target;
			public int SourceConnectedWire = -1; // index of an earlier wire in the snapshot, or -1
			public int TargetConnectedWire = -1;
			public Vector2 SourceConnectionPoint;
			public Vector2 TargetConnectionPoint;
			public int SourceSegmentIndex;
			public int TargetSegmentIndex;
			public Vector2[] Points;
		}

		sealed class ElementsSnapshot
		{
			public readonly List<ElementSnapshot> Elements = new();
			public readonly List<WireSnapshot> Wires = new();
		}

		// Shared by all chips and projects for as long as the app runs
		static ElementsSnapshot clipboard;

		public static bool ClipboardHasContent => clipboard != null && clipboard.Elements.Count > 0;

		ElementsSnapshot CreateSnapshot(IEnumerable<IMoveable> elements)
		{
			List<IMoveable> list = elements.Where(e => e != null).Distinct().ToList();
			IMoveable[] elementsToCopy = list.Concat(GetNonIncludedLinkedBusElements(list)).Where(e => e != null).Distinct().ToArray();

			ElementsSnapshot snapshot = new();
			HashSet<int> copiedIDs = new();

			foreach (IMoveable element in elementsToCopy)
			{
				ElementSnapshot entry = new()
				{
					OriginalID = element.ID,
					Position = element.Position,
					AnchorPosition = element is DevPinInstance p ? p.HandlePosition : element.Position
				};

				if (element is DevPinInstance devPin)
				{
					entry.IsDevPin = true;
					entry.IsInputPin = devPin.IsInputPin;
					entry.PinDesc = DescriptionCreator.CreatePinDescription(devPin);
				}
				else
				{
					SubChipInstance subChip = (SubChipInstance)element;
					entry.ChipDesc = subChip.Description;
					entry.SubChipDesc = DescriptionCreator.CreateSubChipDescription(subChip);
				}

				snapshot.Elements.Add(entry);
				copiedIDs.Add(element.ID);
			}

			Dictionary<WireInstance, int> wireIndex = new();
			foreach (WireInstance wire in ActiveDevChip.Wires)
			{
				if (!copiedIDs.Contains(wire.SourcePin.Address.PinOwnerID) || !copiedIDs.Contains(wire.TargetPin.Address.PinOwnerID)) continue;

				WireSnapshot entry = new()
				{
					Source = wire.SourcePin.Address,
					Target = wire.TargetPin.Address,
					SourceConnectionPoint = wire.SourceConnectionInfo.connectionPoint,
					TargetConnectionPoint = wire.TargetConnectionInfo.connectionPoint,
					SourceSegmentIndex = wire.SourceConnectionInfo.wireConnectionSegmentIndex,
					TargetSegmentIndex = wire.TargetConnectionInfo.wireConnectionSegmentIndex,
					Points = new Vector2[wire.WirePointCount]
				};

				if (wire.SourceConnectionInfo.connectedWire != null && wireIndex.TryGetValue(wire.SourceConnectionInfo.connectedWire, out int sourceWireIndex)) entry.SourceConnectedWire = sourceWireIndex;
				if (wire.TargetConnectionInfo.connectedWire != null && wireIndex.TryGetValue(wire.TargetConnectionInfo.connectedWire, out int targetWireIndex)) entry.TargetConnectedWire = targetWireIndex;
				for (int i = 0; i < entry.Points.Length; i++) entry.Points[i] = wire.GetWirePoint(i);

				wireIndex.Add(wire, snapshot.Wires.Count);
				snapshot.Wires.Add(entry);
			}

			return snapshot;
		}

		// Starts placing a copy of the snapshot, following the mouse. With resolveFromLibrary, chips are
		// looked up by name in this project (the snapshot may come from another chip or project), and
		// chips that are missing or cannot go inside the viewed chip are left out, with their wires.
		bool PlaceSnapshot(ElementsSnapshot snapshot, bool resolveFromLibrary)
		{
			// ---- Decide which elements can be placed ----
			Dictionary<ElementSnapshot, ChipDescription> chipDescriptions = new();
			foreach (ElementSnapshot entry in snapshot.Elements)
			{
				if (entry.IsDevPin) continue;

				ChipDescription chipDesc = entry.ChipDesc;
				if (resolveFromLibrary)
				{
					if (!project.chipLibrary.TryGetChipDescription(entry.SubChipDesc.Name, out chipDesc)) continue;
					if (!ActiveDevChip.CanAddSubchip(chipDesc.Name)) continue;
				}

				chipDescriptions.Add(entry, chipDesc);
			}

			HashSet<int> placeableIDs = snapshot.Elements.Where(e => e.IsDevPin || chipDescriptions.ContainsKey(e)).Select(e => e.OriginalID).ToHashSet();
			Dictionary<int, int> busPairOfOriginal = new();
			List<ElementSnapshot> toPlace = new();

			foreach (ElementSnapshot entry in snapshot.Elements)
			{
				if (!placeableIDs.Contains(entry.OriginalID)) continue;

				// A bus is only usable together with its other end
				if (!entry.IsDevPin && ChipTypeHelper.IsBusType(chipDescriptions[entry].ChipType))
				{
					uint[] data = entry.SubChipDesc.InternalData;
					if (data == null || data.Length == 0 || !placeableIDs.Contains((int)data[0])) continue;
					if (ChipTypeHelper.IsBusTerminusType(chipDescriptions[entry].ChipType)) busPairOfOriginal[entry.OriginalID] = (int)data[0];
				}

				toPlace.Add(entry);
			}

			if (toPlace.Count == 0) return false;

			// ---- Create and start placing the elements ----
			List<IMoveable> placedElements = new(toPlace.Count);
			Dictionary<int, int> newIDFromOriginalID = new();

			foreach (ElementSnapshot entry in toPlace)
			{
				int instanceID = IDGenerator.GenerateNewElementID(ActiveDevChip);
				IMoveable element;

				if (entry.IsDevPin)
				{
					PinDescription pinDesc = entry.PinDesc;
					pinDesc.ID = instanceID;
					element = new DevPinInstance(pinDesc, entry.IsInputPin);
				}
				else
				{
					SubChipDescription subChipDesc = entry.SubChipDesc;
					subChipDesc.ID = instanceID;
					subChipDesc.InternalData = (uint[])subChipDesc.InternalData?.Clone();
					element = new SubChipInstance(chipDescriptions[entry], subChipDesc);
				}

				StartPlacing(element, entry.Position, true);
				element.StraightLineReferencePoint = entry.Position;
				// A pasted copy has no sensible line to keep to (the original may be anywhere)
				if (resolveFromLibrary) element.HasReferencePointForStraightLineMovement = false;

				placedElements.Add(element);
				newIDFromOriginalID.Add(entry.OriginalID, instanceID);
			}

			LinkDuplicatedBuses(placedElements, busPairOfOriginal);

			// ---- Recreate the wires between them ----
			DuplicatedWires.Clear();
			WireInstance[] createdWires = new WireInstance[snapshot.Wires.Count];

			for (int w = 0; w < snapshot.Wires.Count; w++)
			{
				WireSnapshot entry = snapshot.Wires[w];
				if (!newIDFromOriginalID.TryGetValue(entry.Source.PinOwnerID, out int sourceID)) continue;
				if (!newIDFromOriginalID.TryGetValue(entry.Target.PinOwnerID, out int targetID)) continue;

				// Pins can be missing or different if a chip pasted by name has changed since it was copied
				if (!DevChipInstance.TryFindPin(placedElements, new PinAddress(sourceID, entry.Source.PinID), out PinInstance sourcePin)) continue;
				if (!DevChipInstance.TryFindPin(placedElements, new PinAddress(targetID, entry.Target.PinID), out PinInstance targetPin)) continue;
				if (!sourcePin.IsSourcePin || targetPin.IsSourcePin || sourcePin.bitCount != targetPin.bitCount) continue;

				WireInstance.ConnectionInfo sourceConnectionInfo = new()
				{
					pin = sourcePin,
					connectedWire = entry.SourceConnectedWire >= 0 ? createdWires[entry.SourceConnectedWire] : null,
					connectionPoint = entry.SourceConnectionPoint,
					wireConnectionSegmentIndex = entry.SourceSegmentIndex
				};

				WireInstance.ConnectionInfo targetConnectionInfo = new()
				{
					pin = targetPin,
					connectedWire = entry.TargetConnectedWire >= 0 ? createdWires[entry.TargetConnectedWire] : null,
					connectionPoint = entry.TargetConnectionPoint,
					wireConnectionSegmentIndex = entry.TargetSegmentIndex
				};

				WireInstance wire = new(sourceConnectionInfo, targetConnectionInfo, (Vector2[])entry.Points.Clone(), ActiveDevChip.Wires.Count + DuplicatedWires.Count);
				createdWires[w] = wire;
				DuplicatedWires.Add(wire);
			}

			// ---- Attach the element nearest the mouse to the mouse ----
			Vector2 mousePos = InputHelper.MousePosWorld;
			Vector2 closestElementPos = Vector2.zero;
			float closestDst = float.MaxValue;

			foreach (ElementSnapshot entry in toPlace)
			{
				float dst = Vector2.Distance(entry.AnchorPosition, mousePos);
				if (dst < closestDst)
				{
					closestDst = dst;
					closestElementPos = entry.AnchorPosition;
				}
			}

			moveElementMouseStartPos -= mousePos - closestElementPos;
			return true;
		}

		void DuplicateElements(List<IMoveable> elements)
		{
			if (elements.Count == 0) return;
			PlaceSnapshot(CreateSnapshot(elements), false);
		}

		public bool CanCopy => HasControl && SelectedElements.Count > 0 && !IsPlacingOrMovingElementOrCreatingWire;

		public void CopySelection()
		{
			if (!CanCopy) return;
			clipboard = CreateSnapshot(SelectedElements);
		}

		public void CutSelection()
		{
			if (!CanCopy) return;
			clipboard = CreateSnapshot(SelectedElements);
			DeleteElements(SelectedElements);
		}

		public void Paste()
		{
			if (!HasControl || !ClipboardHasContent || IsPlacingOrMovingElementOrCreatingWire) return;
			PlaceSnapshot(clipboard, true);
		}

		public void SelectAll()
		{
			if (!HasControl || IsPlacingOrMovingElementOrCreatingWire) return;

			ClearSelection();
			foreach (IMoveable element in ActiveDevChip.Elements) Select(element);
		}

		void DuplicateSelectedElements()
		{
			DuplicateElements(SelectedElements);
		}

		// ---- Actions for the touch interface's buttons ----

		public bool IsEditingWire => wireToEdit != null;
		public bool IsPlacingCopies => isPlacingNewElements && newElementsAreDuplicatedElements;

		// Copies the selection and puts the copy straight down beside it (on the first free side),
		// then selects the copy so it can be dragged into place. A touch screen has no pointer
		// for a copy to follow, unlike Ctrl+D on desktop.
		public void DuplicateSelection()
		{
			if (!HasControl || SelectedElements.Count == 0 || IsPlacingOrMovingElementOrCreatingWire) return;

			Bounds2D bounds = Bounds2D.CreateEmpty();
			foreach (IMoveable element in SelectedElements) bounds = Bounds2D.Grow(bounds, element.SelectionBoundingBox);
			IMoveable[] obstacles = ActiveDevChip.Elements.ToArray();

			float gap = DrawSettings.GridSize * 4;
			float dx = Mathf.Ceil((bounds.Width + gap) / DrawSettings.GridSize) * DrawSettings.GridSize;
			float dy = Mathf.Ceil((bounds.Height + gap) / DrawSettings.GridSize) * DrawSettings.GridSize;
			Vector2[] offsets = { new(dx, 0), new(0, -dy), new(-dx, 0), new(0, dy), new(dx, -dy), new(dx * 2, 0) };

			DuplicateElements(SelectedElements);
			List<IMoveable> copies = new(SelectedElements);

			Vector2 offset = offsets[0];
			bool free = false;
			foreach (Vector2 candidate in offsets)
			{
				offset = candidate;
				free = true;
				foreach (IMoveable copy in copies)
				{
					copy.Position = copy.MoveStartPosition + offset;
					if (obstacles.Any(o => copy.BoundingBox.Overlaps(o.BoundingBox))) free = false;
				}

				if (free) break;
			}

			foreach (IMoveable copy in copies) copy.IsValidMovePos = free;
			foreach (WireInstance wire in DuplicatedWires) wire.MoveOffset = offset;

			// With no free side the copy stays attached, to be placed with the next tap.
			if (!free) return;

			FinishPlacingNewElements();
			for (int i = 0; i < copies.Count; i++) Select(copies[i], i > 0);
		}

		public void DeleteSelection()
		{
			if (!HasControl || SelectedElements.Count == 0 || IsPlacingOrMovingElementOrCreatingWire) return;
			DeleteElements(SelectedElements);
		}

		// Places the elements being placed where they are now (they follow the finger);
		// returns false if they overlap something and stay unplaced.
		public bool TryFinishPlacing()
		{
			if (!isPlacingNewElements) return false;
			FinishPlacingNewElements();
			return !isPlacingNewElements;
		}

		// Removes the last bend of the wire being drawn; cancels the wire if it has none.
		public void RemoveLastWirePoint()
		{
			if (IsCreatingWire && !WireToPlace.RemoveLastPoint()) CancelEverything();
		}

		public void FinishWireEdit() => ExitWireEditMode();

		public void Select(IMoveable element, bool addToCurrentSelection = true)
		{
			ExitWireEditMode();

			if (element.IsSelected)
			{
				// If in add mode, and element already selected, then remove it from the selection
				if (addToCurrentSelection)
				{
					element.IsSelected = false;
					SelectedElements.Remove(element);
				}
			}
			else
			{
				if (!addToCurrentSelection)
				{
					ClearSelection();
				}

				SelectedElements.Add(element);
				element.IsSelected = true;
				element.IsValidMovePos = true;
			}
		}

		void HandleRightMouseDown()
		{
			// Cancel placement by right-clicking
			if (IsPlacingOrMovingElementOrCreatingWire)
			{
				CancelEverything();
				InputHelper.ConsumeMouseButtonDownEvent(MouseButton.Right);
			}

			IsCreatingSelectionBox = false;
			ClearSelection();
		}

		void HandleLeftMouseDown()
		{
			SelectionBoxStartPos = InputHelper.MousePosWorld;
			straightLineMoveState = StraightLineMoveState.None;

			if (InteractionState.ElementUnderMouse == null) ExitWireEditMode();

			if (InteractionState.MouseIsOverUI) return;

			// Confirm placement of new item
			if (IsPlacingElementOrCreatingWire)
			{
				// Place wire
				if (IsCreatingWire) //
				{
					if (TryFinishPlacingWire())
					{
						CancelPlacingItems();
					}
					else if (CanAddWirePoint())
					{
						WireToPlace.AddWirePoint(InputHelper.MousePosWorld);
					}
				}
				// Place subchip / devpin
				else
				{
					FinishPlacingNewElements();
				}
			}
			else
			{
				// Mouse down on pin: start placing wire
				if (InteractionState.ElementUnderMouse is PinInstance pin && HasControl)
				{
					WireInstance.ConnectionInfo connectionInfo = new() { pin = pin };
					StartPlacingWire(connectionInfo);
				}
				// Mouse down on wire
				else if (InteractionState.ElementUnderMouse is WireInstance wire && HasControl)
				{
					// Insert a point on the currently edited wire
					if (wire == wireToEdit)
					{
						if (wireEditCanInsertPoint)
						{
							(Vector2 point, int segmentIndex) = WireLayoutHelper.GetClosestPointOnWire(wireToEdit, InputHelper.MousePosWorld);
							wireToEdit.InsertPoint(point, segmentIndex);
							wireEditPointIndex = segmentIndex + 1;
						}
					}
					// Start placing a new wire from this point on the selected wire
					else
					{
						WireInstance.ConnectionInfo connectionInfo = CreateWireToWireConnectionInfo(wire, wire.SourcePin);
						StartPlacingWire(connectionInfo);
					}
				}
				// Mouse down on selectable element: select it and prepare to start moving current selection
				else if (InteractionState.ElementUnderMouse is IMoveable element)
				{
					bool addToSelection = KeyboardShortcuts.MultiModeHeld;
					Select(element, addToSelection);
					StartMovingSelectedItems();
				}
				// Mouse down over nothing: clear selection
				else if (InteractionState.ElementUnderMouse == null && !IsPlacingElementOrCreatingWire)
				{
					if (!KeyboardShortcuts.MultiModeHeld) ClearSelection(); // don't clear if in 'multi-mode' (to allow box selecting multiple times)

					// On a touch screen a finger on empty space moves the view, unless area
					// selection is switched on.
					if (MobileUI.IsActive && !MobileUI.AreaSelectMode) CameraController.StartDragPan();
					else IsCreatingSelectionBox = true;
				}

				if (wireToEdit != null && wireEditPointIndex != -1)
				{
					isMovingWireEditPoint = true;
					wireEditPointSelectedIndex = wireEditPointIndex;
					wireEditPointOld = wireToEdit.GetWirePoint(wireEditPointIndex);
				}
			}
		}

		WireInstance.ConnectionInfo CreateWireToWireConnectionInfo(WireInstance wireToConnectTo, PinInstance pin)
		{
			Vector2 mousePos = InputHelper.MousePosWorld;
			if (project.ShouldSnapToGrid) mousePos = GridHelper.SnapToGrid(mousePos, true, true);

			// If connecting a new wire to an existing wire, the target connection point is end pos of new wire (this is mouse pos but with snapping options applied)
			// Otherwise if creating a new wire from an existing wire, connection point is at mouse pos.
			Vector2 targetPoint = WireToPlace?.GetWirePoint(WireToPlace.WirePointCount - 1) ?? mousePos;
			// Find where target connection point is closest to the target wire.
			(Vector2 bestPoint, int bestSegmentIndex) = WireLayoutHelper.GetClosestPointOnWire(wireToConnectTo, targetPoint);

			return new WireInstance.ConnectionInfo
			{
				pin = pin,
				connectedWire = wireToConnectTo,
				wireConnectionSegmentIndex = bestSegmentIndex,
				connectionPoint = bestPoint
			};
		}

		void StartPlacingWire(WireInstance.ConnectionInfo connectionInfo)
		{
			ExitWireEditMode();
			ClearSelection();
			int spawnOrder = project.ViewedChip.Wires.Count > 0 ? project.ViewedChip.Wires[^1].spawnOrder + 1 : 0;
			WireToPlace = new WireInstance(connectionInfo, spawnOrder);
		}

		void FinishMovingElements()
		{
			// -- If any elements are in invalid position, cancel the movement --
			bool hasMoved = false;

			foreach (IMoveable element in SelectedElements)
			{
				if (!element.IsValidMovePos)
				{
					CancelMovingSelectedItems();
					return;
				}

				hasMoved |= (element.MoveStartPosition != element.Position);
			}

			if (hasMoved) ActiveDevChip.UndoController.RecordMoveElements(SelectedElements);

			// -- Apply movement --
			IsMovingSelection = false;

			foreach (WireInstance wire in ActiveDevChip.Wires)
			{
				wire.ApplyMoveOffset();
			}
		}

		void FinishPlacingNewElements()
		{
			// ---- If any elements are in invalid position, don't allow the placement ----
			foreach (IMoveable element in SelectedElements)
			{
				if (!element.IsValidMovePos)
				{
					return;
				}
			}

			List<IMoveable> newlyPlacedElements = new(SelectedElements);

			// ---- Add newly placed elements to the chip ----
			foreach (IMoveable elementToPlace in SelectedElements)
			{
				if (elementToPlace is SubChipInstance subchip)
				{
					ActiveDevChip.AddNewSubChip(subchip, false);
				}
				else if (elementToPlace is DevPinInstance devPin) ActiveDevChip.AddNewDevPin(devPin, false);
			}

			foreach (WireInstance wire in DuplicatedWires)
			{
				ActiveDevChip.AddWire(wire, false);
				wire.ApplyMoveOffset();
			}

			ActiveDevChip.UndoController.RecordAddElements(SelectedElements, DuplicatedWires.Count > 0);
			DuplicatedWires.Clear();
			OnFinishedPlacingItems();


			if (KeyboardShortcuts.MultiModeHeld)
			{
				DuplicateElements(newlyPlacedElements);
			}
		}

		public void EnterWireEditMode(WireInstance wire)
		{
			if (wireToEdit == wire) ExitWireEditMode();
			else wireToEdit = wire;
		}

		void ExitWireEditMode()
		{
			if (wireToEdit != null && isMovingWireEditPoint)
			{
				wireToEdit.SetWirePoint(wireEditPointOld, wireEditPointSelectedIndex);
			}

			wireToEdit = null;
			isMovingWireEditPoint = false;
			wireEditPointIndex = -1;
			wireEditPointSelectedIndex = -1;
		}

		void HandleLeftMouseUp()
		{
			// Place items that are being moved
			if (!IsPlacingElementOrCreatingWire)
			{
				if (IsMovingSelection)
				{
					FinishMovingElements();
				}
			}

			// Select all selectable elements inside selection box
			if (IsCreatingSelectionBox)
			{
				if (!KeyboardShortcuts.MultiModeHeld) ClearSelection();
				IsCreatingSelectionBox = false;

				float selectionBoxArea = Mathf.Abs(SelectionBoxSize.x * SelectionBoxSize.y);
				if (selectionBoxArea > 0.000001f)
				{
					foreach (IMoveable element in ActiveDevChip.Elements)
					{
						if (element.ShouldBeIncludedInSelectionBox(SelectionBoxCentre, SelectionBoxSize))
						{
							Select(element);
						}
					}
				}
			}


			if (IsCreatingWire)
			{
				if (TryFinishPlacingWire())
				{
					CancelPlacingItems();
				}
			}

			if (wireToEdit != null)
			{
				wireEditPointSelectedIndex = -1;
				isMovingWireEditPoint = false;
			}
		}


		void UpdatePositionsToMouse()
		{
			Vector2 mousePos = InputHelper.MousePosWorld;
			bool snapToGrid = project.ShouldSnapToGrid;

			if (IsCreatingWire)
			{
				WireToPlace.SetLastWirePoint(mousePos);
			}
			else if (IsMovingSelection || isPlacingNewElements)
			{
				Vector2 moveOffset = mousePos - moveElementMouseStartPos;

				for (int i = 0; i < SelectedElements.Count; i++)
				{
					IMoveable element = SelectedElements[i];
					bool isBusTerminus = element is SubChipInstance s && ChipTypeHelper.IsBusTerminusType(s.ChipType);
					Vector2 multiElementOffset = isBusTerminus ? Vector2.zero : Vector2.down * (itemPlacementCurrVerticalSpacing * i);

					Vector2 totalOffset = moveOffset + multiElementOffset;
					Vector2 targetPos = element.MoveStartPosition + totalOffset;

					if (snapToGrid)
					{
						if (i == 0)
						{
							targetPos = GridHelper.SnapMovingElementToGrid(element, totalOffset, false, true);
						}
						// Snap additional selected elements relative to the first one. (Snapping each element independently results in a 'jiggling' effect)
						else
						{
							// Get snap points prior to movement
							IMoveable prevElement = SelectedElements[i - 1];
							Vector2 snapPointStartA = prevElement.MoveStartPosition + (prevElement.SnapPoint - prevElement.Position);
							Vector2 snapPointStartB = element.MoveStartPosition + (element.SnapPoint - element.Position);
							// Base curr element's snap pos on prev element, adding the (snapped) difference between their initial snap points
							Vector2 placementManualOffset = isBusTerminus ? Vector2.zero : Vector2.down * itemPlacementCurrVerticalSpacing;

							Vector2 snappedOffset = GridHelper.SnapToGrid(snapPointStartB - snapPointStartA + placementManualOffset, false, true);
							Vector2 elementSnapPointOffset = element.SnapPoint - element.Position;
							targetPos = prevElement.SnapPoint + snappedOffset - elementSnapPointOffset;
						}
					}

					// When using shift to duplicate new element, don't use straight line mode unless pressed again
					if (isPlacingNewElements && !KeyboardShortcuts.MultiModeHeld) hasExittedMultiModeSincePlacementStart = true;

					if (KeyboardShortcuts.StraightLineModeHeld && element.HasReferencePointForStraightLineMovement && (!isPlacingNewElements || hasExittedMultiModeSincePlacementStart))
					{
						Vector2 offset = targetPos - element.StraightLineReferencePoint;
						float ox = Mathf.Abs(offset.x);
						float oy = Mathf.Abs(offset.y);
						bool canChangeState = straightLineMoveState == StraightLineMoveState.None || isPlacingNewElements;
						if (Mathf.Max(ox, oy) > 0.035f && canChangeState)
						{
							straightLineMoveState = ox > oy ? StraightLineMoveState.Horizontal : StraightLineMoveState.Vertical;
						}

						if (straightLineMoveState == StraightLineMoveState.Horizontal) offset.y = 0;
						else if (straightLineMoveState == StraightLineMoveState.Vertical) offset.x = 0;
						targetPos = element.StraightLineReferencePoint + offset;
					}

					element.Position = targetPos;

					// Test if is legal position
					bool legal = true;
					foreach (IMoveable obstacle in Obstacles)
					{
						if (element.BoundingBox.Overlaps(obstacle.BoundingBox))
						{
							legal = false;
							break;
						}
					}

					element.IsValidMovePos = legal;
				}


				// Update wires when their parents are moved
				if (isPlacingNewElements)
				{
					foreach (WireInstance wire in DuplicatedWires)
					{
						Vector2 delA = wire.SourcePin.parent.Position - wire.SourcePin.parent.MoveStartPosition;
						Vector2 delB = wire.TargetPin.parent.Position - wire.TargetPin.parent.MoveStartPosition;
						// Parent chips may have been moved by slightly different amounts if snapping is enabled, so just take average
						wire.MoveOffset = (delA + delB) / 2;
					}
				}
				else
				{
					foreach (WireInstance wire in ActiveDevChip.Wires)
					{
						// If both ends of the wire are being moved, then move the entire wire
						if (IsSelected(wire.SourcePin.parent) && IsSelected(wire.TargetPin.parent))
						{
							Vector2 delA = wire.SourcePin.parent.Position - wire.SourcePin.parent.MoveStartPosition;
							Vector2 delB = wire.TargetPin.parent.Position - wire.TargetPin.parent.MoveStartPosition;
							// Parent chips may have been moved by slightly different amounts if snapping is enabled, so just take average
							wire.MoveOffset = (delA + delB) / 2;
						}
					}
				}
			}
			else if (isMovingWireEditPoint)
			{
				wireToEdit.SetWirePointWithSnapping(mousePos, wireEditPointSelectedIndex, wireEditPointOld);
			}
		}


		void StartMovingSelectedItems()
		{
			IsMovingSelection = true;
			moveElementMouseStartPos = InputHelper.MousePosWorld;

			foreach (IMoveable moveableElement in SelectedElements)
			{
				moveableElement.MoveStartPosition = moveableElement.Position;
				moveableElement.StraightLineReferencePoint = moveableElement.Position;
				moveableElement.HasReferencePointForStraightLineMovement = true;
			}

			Obstacles = ActiveDevChip.Elements.Where(e => !e.IsSelected).ToArray();
		}

		bool TryFinishPlacingWire()
		{
			if (InteractionState.ElementUnderMouse is PinInstance pin)
			{
				if (CanCompleteWireConnection(pin))
				{
					WireInstance.ConnectionInfo info = new() { pin = pin };
					CompleteConnection(info);
					return true;
				}
			}
			else if (InteractionState.ElementUnderMouse is WireInstance connectionWire)
			{
				if (CanCompleteWireConnection(connectionWire, out PinInstance endPin))
				{
					WireInstance.ConnectionInfo info = CreateWireToWireConnectionInfo(connectionWire, endPin);
					CompleteConnection(info);
					return true;
				}
			}

			return false;

			void CompleteConnection(WireInstance.ConnectionInfo info)
			{
				WireToPlace.FinishPlacingWire(info);
				ActiveDevChip.AddWire(WireToPlace, false);
				ActiveDevChip.UndoController.RecordAddWire(WireToPlace);
			}
		}

		bool CanAddWirePoint()
		{
			// Can add wire point if mouse is not over anything else
			if (InteractionState.ElementUnderMouse is null) return true;

			// Can add wire point if mouse is over an existing wire, but that wire comes from same pin as current wire (might want to trace over existing wire for example)
			if (InteractionState.ElementUnderMouse is WireInstance wire)
			{
				if (wire.SourcePin == WireToPlace.FirstPin || wire.TargetPin == WireToPlace.FirstPin) return true;
			}

			return false;
		}

		public void StartPlacing(string name)
		{
			StartPlacing(project.chipLibrary.GetChipDescription(name));
		}

		public void StartPlacing(ChipDescription chipDescription)
		{
			StartPlacing(chipDescription, InputHelper.MousePosWorld, false);
		}

		public IMoveable StartPlacing(ChipDescription chipDescription, Vector2 position, bool isDuplicating)
		{
			IMoveable elementToPlace = CreateElementFromChipDescription(chipDescription);
			StartPlacing(elementToPlace, position, isDuplicating);
			return elementToPlace;
		}

		void StartPlacing(IMoveable elementToPlace, Vector2 position, bool isDuplicating)
		{
			const float busPairSpacing = DrawSettings.GridSize * 8;
			newElementsAreDuplicatedElements = isDuplicating;

			if (!isPlacingNewElements)
			{
				CancelEverything();
				isPlacingNewElements = true;
				hasExittedMultiModeSincePlacementStart = false;
				StartMovingSelectedItems();
			}

			ChipType chipType;
			if (elementToPlace is DevPinInstance devPin) chipType = ChipTypeHelper.GetPinType(devPin.IsInputPin, devPin.BitCount);
			else chipType = ((SubChipInstance)elementToPlace).ChipType;

			// Place bus terminus to right of bus origin
			if (ChipTypeHelper.IsBusTerminusType(chipType) && !isDuplicating)
			{
				elementToPlace.MoveStartPosition = SelectedElements[^1].MoveStartPosition + Vector2.right * busPairSpacing;
				elementToPlace.HasReferencePointForStraightLineMovement = false;
			}
			// If placing multiple elements simultaneously, place the new element below the previous one
			// (unless duplicating elements, in which case their relative positions should be preserved)
			else if (SelectedElements.Count > 0 && !isDuplicating)
			{
				float spacing = (elementToPlace.SelectionBoundingBox.Size.y + SelectedElements[^1].SelectionBoundingBox.Size.y) / 2;

				Vector2 prevElementPos = SelectedElements[^1].MoveStartPosition;
				// If prev element was bus terminus, we want the midpoint between the bus origin and terminus pair
				if (SelectedElements[^1] is SubChipInstance s && ChipTypeHelper.IsBusTerminusType(s.ChipType))
				{
					prevElementPos = (prevElementPos + SelectedElements[^2].MoveStartPosition) / 2;
				}

				elementToPlace.MoveStartPosition = prevElementPos + Vector2.down * spacing;
				elementToPlace.HasReferencePointForStraightLineMovement = false;
			}
			else
			{
				moveElementMouseStartPos = InputHelper.MousePosWorld;
				elementToPlace.MoveStartPosition = position;
				elementToPlace.StraightLineReferencePoint = position;
				elementToPlace.HasReferencePointForStraightLineMovement = isDuplicating;
			}

			Select(elementToPlace);

			// When placing bus, auto-place the corresponding bus terminus (unless duplicating an existing bus)
			if (ChipTypeHelper.IsBusOriginType(chipType) && !isDuplicating)
			{
				elementToPlace.MoveStartPosition -= Vector2.right * busPairSpacing / 2;

				ChipType terminusType = ChipTypeHelper.GetCorrespondingBusTerminusType(chipType);
				ChipDescription terminusDescription = Project.ActiveProject.chipLibrary.GetChipDescription(ChipTypeHelper.GetName(terminusType));
				SubChipInstance terminus = (SubChipInstance)StartPlacing(terminusDescription, position, false);

				SubChipInstance busOrigin = (SubChipInstance)elementToPlace;
				busOrigin.SetLinkedBusPair(terminus);
				terminus.SetLinkedBusPair(busOrigin);
			}
		}

		IMoveable CreateElementFromChipDescription(ChipDescription chipDescription)
		{
			IMoveable elementToPlace;
			int instanceID = IDGenerator.GenerateNewElementID(ActiveDevChip);

			// Input/output dev pins are represented as chips for convenience
			(bool isInput, bool isOutput, PinBitCount numBits) ioPinInfo = ChipTypeHelper.IsInputOrOutputPin(chipDescription.ChipType);

			if (ioPinInfo.isInput || ioPinInfo.isOutput) // Dev pin
			{
				PinDescription pinDesc = ioPinInfo.isInput ? chipDescription.InputPins[0] : chipDescription.OutputPins[0];
				pinDesc.ID = instanceID;
				elementToPlace = new DevPinInstance(pinDesc, ioPinInfo.isInput);
			}

			else // SubChip
			{
				SubChipDescription subChipDesc = DescriptionCreator.CreateBuiltinSubChipDescriptionForPlacement(chipDescription.ChipType, chipDescription.Name, instanceID, Vector2.zero);
				elementToPlace = new SubChipInstance(chipDescription, subChipDesc);
			}

			return elementToPlace;
		}

		public void CancelEverything()
		{
			CancelMovingSelectedItems();
			CancelPlacingItems();
			ClearSelection();
			IsCreatingSelectionBox = false;
			isPlacingNewElements = false;
			ExitWireEditMode();
		}

		void ClearSelection()
		{
			foreach (IMoveable element in SelectedElements)
			{
				element.IsSelected = false;
				element.IsValidMovePos = true;
			}

			SelectedElements.Clear();
		}

		void CancelMovingSelectedItems()
		{
			if (IsMovingSelection && SelectedElements.Count > 0)
			{
				foreach (IMoveable moveableElement in SelectedElements)
				{
					moveableElement.Position = moveableElement.MoveStartPosition;
				}

				foreach (WireInstance wire in ActiveDevChip.Wires)
				{
					wire.MoveOffset = Vector2.zero;
				}
			}

			IsMovingSelection = false;
		}

		void OnFinishedPlacingItems() => OnFinishedOrCancelledPlacingItems();

		void CancelPlacingItems()
		{
			// If canceling placement of bus terminus, destroy the linked bus origin 
			if (isPlacingNewElements)
			{
				DuplicatedWires.Clear();

				foreach (IMoveable element in SelectedElements)
				{
					if (element is SubChipInstance subChipInstance && subChipInstance.IsBus)
					{
						ActiveDevChip.TryDeleteSubChipByID(subChipInstance.LinkedBusPairID);
					}
				}
			}

			OnFinishedOrCancelledPlacingItems();
		}


		void OnFinishedOrCancelledPlacingItems()
		{
			ClearSelection();
			IsMovingSelection = false;

			isPlacingNewElements = false;
			newElementsAreDuplicatedElements = false;
			WireToPlace = null;
			itemPlacementCurrVerticalSpacing = 0;
		}

		enum StraightLineMoveState
		{
			None,
			Horizontal,
			Vertical
		}
	}
}