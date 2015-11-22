﻿/*
    Copyright (C) 2014-2015 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using dnlib.DotNet;
using dnSpy.Contracts.Themes;
using dnSpy.Shared.UI.AvalonEdit;
using dnSpy.Shared.UI.Decompiler;
using dnSpy.Shared.UI.Highlighting;
using dnSpy.Shared.UI.MVVM;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using ICSharpCode.Decompiler;

namespace dnSpy.Files.Tabs.TextEditor {
	sealed partial class TextEditorControl : UserControl {
		static TextEditorControl() {
			HighlightingManager.Instance.RegisterHighlighting(
				"IL", new string[] { ".il" }, () => {
					using (var s = typeof(TextEditorControl).Assembly.GetManifestResourceStream(typeof(TextEditorControl), "IL.xshd")) {
						using (var reader = new XmlTextReader(s))
							return HighlightingLoader.Load(reader, HighlightingManager.Instance);
					}
				}
			);
		}

		public IClickedReferenceHandler ClickedReferenceHandler {
			set {
				if (value == null)
					throw new ArgumentNullException();
				if (clickedReferenceHandler != null)
					throw new InvalidOperationException();
				clickedReferenceHandler = value;
			}
		}
		IClickedReferenceHandler clickedReferenceHandler;

		public NewTextEditor TextEditor {
			get { return textEditor; }
		}
		readonly NewTextEditor textEditor;

		readonly IThemeManager themeManager;

		readonly IconBarMargin iconBarMargin;
		readonly SearchPanel searchPanel;

		internal DefinitionLookup DefinitionLookup {
			get { return definitionLookup; }
		}
		DefinitionLookup definitionLookup;
		TextSegmentCollection<ReferenceSegment> references;
		readonly TextMarkerService textMarkerService;
		readonly List<ITextMarker> markedReferences = new List<ITextMarker>();

		readonly ReferenceElementGenerator referenceElementGenerator;
		readonly UIElementGenerator uiElementGenerator;
		List<VisualLineElementGenerator> activeCustomElementGenerators = new List<VisualLineElementGenerator>();

		public TextEditorControl(IThemeManager themeManager) {
			this.themeManager = themeManager;
			InitializeComponent();

			Loaded += TextEditorControl_Loaded;
			themeManager.ThemeChanged += ThemeManager_ThemeChanged;

			textEditor = new NewTextEditor(themeManager);
			RemoveCommands(TextEditor);
			newTextEditor.Content = TextEditor;
			TextEditor.IsReadOnly = true;
			TextEditor.ShowLineNumbers = true;
			TextEditor.Options.RequireControlModifierForHyperlinkClick = false;

			referenceElementGenerator = new ReferenceElementGenerator(JumpToReference, a => true);
			// Add the ref elem generator first in case one of the refs looks like a http link etc
			TextEditor.TextArea.TextView.ElementGenerators.Insert(0, referenceElementGenerator);
			this.uiElementGenerator = new UIElementGenerator();
			textEditor.TextArea.TextView.ElementGenerators.Add(uiElementGenerator);

			//TODO: Read from settings
			TextEditor.FontFamily = (FontFamily)new FontFamilyConverter().ConvertFromInvariantString("Consolas");
			TextEditor.FontSize = (double)new FontSizeConverter().ConvertFromInvariantString("10pt");

			iconBarMargin = new IconBarMargin(this);
			TextEditor.TextArea.LeftMargins.Insert(0, iconBarMargin);
			TextEditor.TextArea.TextView.VisualLinesChanged += delegate { iconBarMargin.InvalidateVisual(); };

			textMarkerService = new TextMarkerService(this);
			TextEditor.TextArea.TextView.BackgroundRenderers.Add(textMarkerService);

			searchPanel = SearchPanel.Install(TextEditor.TextArea);
			searchPanel.RegisterCommands(this.CommandBindings);

			TextEditor.TextArea.Caret.PositionChanged += Caret_PositionChanged;

			InputBindings.Add(new KeyBinding(new RelayCommand(a => MoveReference(true)), Key.Tab, ModifierKeys.None));
			InputBindings.Add(new KeyBinding(new RelayCommand(a => MoveReference(false)), Key.Tab, ModifierKeys.Shift));
			InputBindings.Add(new KeyBinding(new RelayCommand(a => FollowReference()), Key.F12, ModifierKeys.None));
			InputBindings.Add(new KeyBinding(new RelayCommand(a => FollowReference()), Key.Enter, ModifierKeys.None));
			InputBindings.Add(new KeyBinding(new RelayCommand(a => FollowReferenceNewTab()), Key.F12, ModifierKeys.Control));
			InputBindings.Add(new KeyBinding(new RelayCommand(a => FollowReferenceNewTab()), Key.Enter, ModifierKeys.Control));
			InputBindings.Add(new KeyBinding(new RelayCommand(a => ClearMarkedReferencesAndPopups()), Key.Escape, ModifierKeys.None));

			this.AddHandler(GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnGotKeyboardFocus), true);
			this.AddHandler(LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnLostKeyboardFocus), true);
		}

		// Remove commands the text editor added so we can use them for our own purposes
		static void RemoveCommands(NewTextEditor textEditor) {
			var handler = textEditor.TextArea.DefaultInputHandler;

			RemoveCommands(handler.Editing);
			RemoveCommands(handler.CaretNavigation);
			RemoveCommands(handler.CommandBindings);
		}

		static void RemoveCommands(ICSharpCode.AvalonEdit.Editing.TextAreaInputHandler handler) {
			var commands = new HashSet<ICommand>();
			var inputList = (IList<InputBinding>)handler.InputBindings;
			for (int i = inputList.Count - 1; i >= 0; i--) {
				var kb = inputList[i] as KeyBinding;
				if (kb == null)
					continue;
				if ((kb.Modifiers == ModifierKeys.None && kb.Key == Key.Back) ||
					(kb.Modifiers == ModifierKeys.Shift && kb.Key == Key.Back) ||
					(kb.Modifiers == ModifierKeys.None && kb.Key == Key.Enter) ||
					(kb.Modifiers == ModifierKeys.None && kb.Key == Key.Tab) ||
					(kb.Modifiers == ModifierKeys.Shift && kb.Key == Key.Tab) ||
					(kb.Modifiers == ModifierKeys.Control && kb.Key == Key.Enter) ||
					(kb.Modifiers == ModifierKeys.None && kb.Key == Key.Delete)) {
					inputList.RemoveAt(i);
					commands.Add(kb.Command);
				}
			}
			RemoveCommands(handler.CommandBindings);
			var bindingList = (IList<CommandBinding>)handler.CommandBindings;
			for (int i = bindingList.Count - 1; i >= 0; i--) {
				var binding = bindingList[i];
				if (commands.Contains(binding.Command))
					bindingList.RemoveAt(i);
			}
		}

		static void RemoveCommands(ICollection<CommandBinding> commandBindings) {
			var bindingList = (IList<CommandBinding>)commandBindings;
			for (int i = bindingList.Count - 1; i >= 0; i--) {
				var binding = bindingList[i];
				if (binding.Command == ICSharpCode.AvalonEdit.AvalonEditCommands.DeleteLine ||
					binding.Command == ApplicationCommands.Undo ||
					binding.Command == ApplicationCommands.Redo ||
					binding.Command == ApplicationCommands.Cut ||
					binding.Command == ApplicationCommands.Delete ||
					binding.Command == EditingCommands.Delete ||
					binding.Command == EditingCommands.Backspace ||
					binding.Command == NavigationCommands.BrowseBack)
					bindingList.RemoveAt(i);
			}
		}

		void ClearCustomElementGenerators() {
			foreach (var elementGenerator in activeCustomElementGenerators)
				textEditor.TextArea.TextView.ElementGenerators.Remove(elementGenerator);
			activeCustomElementGenerators.Clear();
		}

		public void SetOutput(ITextOutput output, IHighlightingDefinition newHighlighting) {
			var avOutput = output as AvalonEditTextOutput;
			Debug.Assert(avOutput != null, "output should be an AvalonEditTextOutput instance");

			TextEditor.LanguageTokens = avOutput == null ? new LanguageTokens() : avOutput.LanguageTokens;
			TextEditor.LanguageTokens.Finish();

			ClearMarkedReferences();
			TextEditor.ScrollToHome();
			TextEditor.Document = null;
			TextEditor.SyntaxHighlighting = newHighlighting;
			ClearCustomElementGenerators();

			if (avOutput == null) {
				uiElementGenerator.UIElements = null;
				referenceElementGenerator.References = null;
				references = new TextSegmentCollection<ReferenceSegment>();
				definitionLookup = null;
				TextEditor.Document = new TextDocument(output.ToString());
			}
			else {
				uiElementGenerator.UIElements = avOutput.UIElements;
				referenceElementGenerator.References = avOutput.References;
				references = avOutput.References;
				definitionLookup = avOutput.DefinitionLookup;
				foreach (var elementGenerator in avOutput.ElementGenerators) {
					TextEditor.TextArea.TextView.ElementGenerators.Add(elementGenerator);
					activeCustomElementGenerators.Add(elementGenerator);
				}

				TextEditor.Document = avOutput.GetDocument();
			}
		}

		public void Clear() {
			ClearMarkedReferences();
			ClearCustomElementGenerators();
			TextEditor.Document = new TextDocument();
			definitionLookup = null;
			uiElementGenerator.UIElements = null;
			referenceElementGenerator.References = null;
			references = new TextSegmentCollection<ReferenceSegment>();
		}

		void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
			TextEditor.Options.HighlightCurrentLine = true;//TODO: Read from settings
		}

		void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) {
			// Do as VS: don't hide the highlighted line
		}

		void ThemeManager_ThemeChanged(object sender, ThemeChangedEventArgs e) {
			var theme = themeManager.Theme;
			var marker = theme.GetColor(ColorType.SearchResultMarker);
			searchPanel.MarkerBrush = marker.Background == null ? Brushes.LightGreen : marker.Background;
			iconBarMargin.InvalidateVisual();
		}

		void TextEditorControl_Loaded(object sender, RoutedEventArgs e) {
			Loaded -= TextEditorControl_Loaded;
			TextEditor.ShowLineMargin(true);//TODO: Read from settings

			TextEditor.TextArea.TextView.VisualLinesChanged += (s, e2) => iconBarMargin.InvalidateVisual();

			// We need to add this here in Loaded and not in the ctor. Adding it in the ctor causes
			// the highlighted line not to be shown when opening a new tab. It's shown again when
			// the caret is moved to another line.
			TextEditor.TextArea.TextView.LineTransformers.Add(textMarkerService);
		}

		void Caret_PositionChanged(object sender, EventArgs e) {
			//TODO: ClosePopups();
			//TODO: CloseToolTip();

			bool autoHighlightRefs = true;//TODO: Read from settings
			if (autoHighlightRefs) {
				int offset = textEditor.TextArea.Caret.Offset;
				var refSeg = GetReferenceSegmentAt(offset);
				if (refSeg != null)
					MarkReferences(refSeg);
				else
					ClearMarkedReferences();
			}
		}

		ReferenceSegment GetCurrentReferenceSegment() {
			return GetReferenceSegmentAt(TextEditor.TextArea.Caret.Offset);
		}

		ReferenceSegment GetReferenceSegmentAt(int offset) {
			if (referenceElementGenerator == null || referenceElementGenerator.References == null)
				return null;
			var segs = referenceElementGenerator.References.FindSegmentsContaining(offset).ToArray();
			foreach (var seg in segs) {
				if (seg.StartOffset <= offset && offset < seg.EndOffset)
					return seg;
			}
			return segs.Length == 0 ? null : segs[0];
		}

		void FollowReference() {
			GoToTarget(GetCurrentReferenceSegment(), true, true);
		}

		void FollowReferenceNewTab() {
			if (clickedReferenceHandler == null)
				return;
			clickedReferenceHandler.GoTo(GetCurrentReferenceSegment(), true, true);
		}

		void ClearMarkedReferencesAndPopups() {
			ClearMarkedReferences();
			//TODO: ClosePopups();
		}

		void MoveReference(bool forward) {
			if (references == null)
				return;
			var refSeg = GetCurrentReferenceSegment();
			if (refSeg == null)
				return;

			foreach (var newSeg in GetReferenceSegmentsFrom(refSeg, forward)) {
				if (RefSegEquals(newSeg, refSeg)) {
					var line = TextEditor.Document.GetLineByOffset(newSeg.StartOffset);
					int column = newSeg.StartOffset - line.Offset + 1;
					ScrollAndMoveCaretTo(line.LineNumber, column);
					break;
				}
			}
		}

		IEnumerable<ReferenceSegment> GetReferenceSegmentsFrom(ReferenceSegment refSeg, bool forward) {
			if (references == null || refSeg == null)
				yield break;

			var currSeg = refSeg;
			while (true) {
				currSeg = forward ? references.GetNextSegment(currSeg) : references.GetPreviousSegment(currSeg);
				if (currSeg == null)
					currSeg = forward ? references.FirstSegment : references.LastSegment;
				if (currSeg == refSeg)
					break;

				yield return currSeg;
			}
		}

		bool GoToTarget(ReferenceSegment refSeg, bool canJumpToReference, bool canRecordHistory) {
			if (clickedReferenceHandler == null)
				return false;
			return clickedReferenceHandler.GoTo(refSeg, false, true);
		}

		internal bool IsOwnerOf(ReferenceSegment refSeg) {
			if (references == null)
				return false;
			foreach (var r in references) {
				if (r == refSeg)
					return true;
			}
			return false;
		}

		ReferenceSegment FindReferenceSegment(ReferenceSegment refSeg) {
			if (references == null || refSeg == null)
				return null;
			foreach (var r in references) {
				if (r.IsLocal == refSeg.IsLocal && r.IsLocalTarget == refSeg.IsLocalTarget && RefSegEquals(r, refSeg))
					return r;
			}
			return null;
		}

		internal ReferenceSegment FindLocalTarget(ReferenceSegment refSeg) {
			if (references == null)
				return null;
			if (refSeg.IsLocalTarget)
				return refSeg;
			foreach (var r in references) {
				if (r.IsLocalTarget && RefSegEquals(r, refSeg))
					return r;
			}
			return null;
		}

		static bool RefSegEquals(ReferenceSegment a, ReferenceSegment b) {
			if (a == b)
				return true;
			if (a == null || b == null)
				return false;
			if (a.Reference == null || b.Reference == null)
				return false;
			if (a.Reference.Equals(b.Reference))
				return true;

			var ma = a.Reference as IMemberRef;
			var mb = b.Reference as IMemberRef;
			if (ma != null && mb != null) {
				ma = Resolve(ma) ?? ma;
				mb = Resolve(mb) ?? mb;
				return new SigComparer(SigComparerOptions.CompareDeclaringTypes | SigComparerOptions.PrivateScopeIsComparable).Equals(ma, mb);
			}

			return false;
		}

		static IMemberRef Resolve(IMemberRef mr) {
			if (mr is ITypeDefOrRef)
				return ((ITypeDefOrRef)mr).ResolveTypeDef();
			if (mr is IMethod && ((IMethod)mr).IsMethod)
				return ((IMethod)mr).ResolveMethodDef();
			if (mr is IField)
				return ((IField)mr).ResolveFieldDef();
			Debug.Assert(mr is PropertyDef || mr is EventDef || mr is GenericParam, "Unknown IMemberRef");
			return null;
		}

		internal void ScrollAndMoveCaretTo(int line, int column, bool focus = true) {
			// Make sure the lines have been re-initialized or the ScrollTo() method could fail
			TextEditor.TextArea.TextView.EnsureVisualLines();
			TextEditor.ScrollTo(line, column);
			TextEditor.SetCaretPosition(line, column);
			if (focus) {
				//TODO: SetTextEditorFocus(this);
			}
		}

		void JumpToReference(ReferenceSegment referenceSegment, MouseEventArgs e) {
			if (clickedReferenceHandler == null)
				return;
			bool newTab = Keyboard.Modifiers == ModifierKeys.Control;
			//TODO: SetActiveView(this);
			TextEditor.GoToMousePosition();
			e.Handled = clickedReferenceHandler.GoTo(referenceSegment, newTab, false);
		}

		internal bool MarkReferences(ReferenceSegment referenceSegment) {
			if (TextEditor.TextArea.TextView.Document == null)
				return false;
			if (previousReferenceSegment == referenceSegment)
				return true;
			object reference = referenceSegment.Reference;
			if (references == null || reference == null)
				return false;
			ClearMarkedReferences();
			previousReferenceSegment = referenceSegment;
			foreach (var tmp in references) {
				var r = tmp;
				if (RefSegEquals(referenceSegment, r)) {
					var mark = textMarkerService.Create(r.StartOffset, r.Length);
					mark.ZOrder = (int)TextLineObjectZOrder.SearchResult;
					mark.HighlightingColor = () => {
						return r.IsLocalTarget ?
							themeManager.Theme.GetTextColor(ColorType.LocalDefinition).ToHighlightingColor() :
							themeManager.Theme.GetTextColor(ColorType.LocalReference).ToHighlightingColor();
					};
					markedReferences.Add(mark);
				}
			}
			return true;
		}
		ReferenceSegment previousReferenceSegment = null;

		void ClearMarkedReferences() {
			foreach (var mark in markedReferences) {
				textMarkerService.Remove(mark);
			}
			markedReferences.Clear();
			previousReferenceSegment = null;
		}
	}
}