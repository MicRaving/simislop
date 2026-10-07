/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using System.Text;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework.Input;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Tests for <see cref="InputManager.ApplyKeyboardInput"/>, which is pure: it reads an
    /// <see cref="UpdateState"/> and mutates a <see cref="StringBuilder"/>. No device, no window,
    /// no window focus needed.
    ///
    /// <para>
    /// The bug being locked in is that <c>UnhandledKeys</c> was never populated, and therefore
    /// that cursor navigation inside a text field was dead. The chain was:
    /// </para>
    /// <list type="number">
    /// <item><c>GameScreen.Update</c> did <c>State.FrameTextInput = TextCharacters</c>, where
    /// <c>TextCharacters</c> is a <c>static List&lt;char&gt;</c> - so
    /// <c>FrameTextInput</c> was never null.</item>
    /// <li>The char-handling block guarded on exactly that:
    /// <c>else if (state.FrameTextInput != null) continue;</c>. Unconditionally taken, so the
    /// keymap below it was never reached and the 135 lines of it were dead on desktop.</item>
    /// <li>With the keymap dead, a key that produced no action of its own ended up nowhere. The
    /// only consumer of <c>UnhandledKeys</c> is <c>UITextEdit</c>'s <c>switch</c> over
    /// Left/Right/Up/Down, so the caret in the chat and EOD inputs could not be moved with the
    /// arrow keys at all.</item>
    /// </list>
    ///
    /// <para>
    /// <b>Which of the two input paths is the real one.</b> The window's <c>TextInput</c> event -
    /// installed by reflection on <c>Window.TextInput</c> by both hosts - delivers one character
    /// per event, through the platform's own layout, with dead keys and IME composition. That is
    /// where text should come from on desktop, and it already worked. The keymap is a US-layout
    /// reconstruction kept as a fallback for a platform with no such event, plus - after this fix -
    /// the test for "does this key produce a character at all?", which is what separates navigation
    /// keys from typing keys. Both survive, and which one is authoritative is now an explicit
    /// property of the input source (<see cref="UpdateState.TextInputAvailable"/>) rather than
    /// something inferred from whether the frame happened to bring any characters, because an
    /// idle frame brings none and a key press and its character event can land in different
    /// frames. Guessing from the count would insert every character twice.
    /// </para>
    /// </summary>
    public sealed class KeyboardInputTests
    {
        private static UpdateState Frame(bool textInputAvailable, List<Keys> newKeys, params char[] characters)
        {
            return new UpdateState
            {
                KeyboardState = new KeyboardState(newKeys.ToArray()),
                PreviousKeyboardState = new KeyboardState(),
                WindowFocused = true,
                TextInputAvailable = textInputAvailable,
                NewKeys = newKeys,
                FrameTextInput = new List<char>(characters),
            };
        }

        private static KeyboardInputResult Apply(UpdateState state, StringBuilder buffer,
            int cursor = -1, int selectionEnd = -1, bool allowInput = true)
        {
            return new InputManager().ApplyKeyboardInput(buffer, state, cursor, selectionEnd, allowInput);
        }

        // ---- navigation keys reach the control ----------------------------------------------------

        [Theory]
        [InlineData(Keys.Left)]
        [InlineData(Keys.Right)]
        [InlineData(Keys.Up)]
        [InlineData(Keys.Down)]
        [InlineData(Keys.Home)]
        [InlineData(Keys.End)]
        public void ANavigationKeyWithNoCharacterIsHandedUpUnchanged(Keys key)
        {
            // UnhandledKeys is the only channel a control has to the raw key, so a key that
            // produces no character has to arrive there, exactly once. Before the fix this list
            // was empty for every key.
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { key });
            var result = Apply(state, new StringBuilder());

            Assert.NotNull(result);
            Assert.Equal(new[] { key }, result.UnhandledKeys);
        }

        [Fact]
        public void AKeyIsListedAtMostOnce()
        {
            // The old shape added the key in the "no action" branch and again in the
            // "TranslateChar produced a control character" branch. UITextEdit moves the caret by
            // one per entry, so a doubled key was a two-character jump.
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.Left });
            var result = Apply(state, new StringBuilder());

            Assert.Single(result.UnhandledKeys);
        }

        [Fact]
        public void NavigationKeysSurviveAFrameThatAlsoDeliversCharacters()
        {
            // Shift+Left is a selection extension in UITextEdit. Shift and Left are both "new
            // keys" this frame, and the character comes through separately - so the character
            // path and the navigation path have to coexist in one frame.
            var state = Frame(textInputAvailable: true,
                newKeys: new List<Keys> { Keys.LeftShift, Keys.Left }, 'x');
            var buffer = new StringBuilder();
            var result = Apply(state, buffer);

            Assert.Contains(Keys.Left, result.UnhandledKeys);
            Assert.Equal("x", buffer.ToString());
        }

        // ---- typing still works, once, through the TextInput path -------------------------------------

        [Fact]
        public void ATypedCharacterArrivesThroughTheTextInputEvent()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.A }, 'a');
            var buffer = new StringBuilder();
            var result = Apply(state, buffer);

            Assert.Equal("a", buffer.ToString());
            Assert.Equal(1, result.NumInsertions);
            Assert.True(result.ContentChanged);
        }

        [Fact]
        public void ACharacterKeyIsNotAlsoReportedAsUnhandled()
        {
            // 'A' was handled - the character was inserted. Calling it unhandled as well would be
            // a lie, and would eventually reach a control that acts on raw keys.
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.A }, 'a');
            var result = Apply(state, new StringBuilder());

            Assert.Empty(result.UnhandledKeys);
        }

        [Fact]
        public void ShiftedCharactersAreNotUnreported()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.A }, 'A');
            var buffer = new StringBuilder();
            var result = Apply(state, buffer);

            Assert.Equal("A", buffer.ToString());
            Assert.Empty(result.UnhandledKeys);
        }

        [Fact]
        public void SeveralCharactersInOneFrameAllArriveInOrder()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys>(), 'h', 'i');
            var buffer = new StringBuilder();
            var result = Apply(state, buffer);

            Assert.Equal("hi", buffer.ToString());
            Assert.Equal(2, result.NumInsertions);
        }

        [Fact]
        public void AKeyAndItsCharacterInTheSameFrameInsertOneCharacterNotTwo()
        {
            // The reason TextInputAvailable is a property of the source rather than a guess from
            // the character count: a key press and its character event can land in different
            // frames, so "no characters this frame" does not mean "use the keymap".
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.A }, 'a');
            var buffer = new StringBuilder();
            Apply(state, buffer);

            Assert.Equal("a", buffer.ToString());
        }

        // ---- the keymap fallback, for a platform with no TextInput event -----------------------------

        [Fact]
        public void WithoutATextInputEventTheKeymapStillProducesCharacters()
        {
            var state = Frame(textInputAvailable: false, newKeys: new List<Keys> { Keys.A });
            var buffer = new StringBuilder();
            var result = Apply(state, buffer);

            Assert.Equal("a", buffer.ToString());
            Assert.Empty(result.UnhandledKeys);
        }

        [Fact]
        public void TheKeymapRespectsShiftAndCapsLock()
        {
            // LeftShift is held, so the keymap has to produce the shifted character. This is the
            // path a platform with no TextInput event would take, and it is the one that must not
            // be used on desktop - see the note on TextInputAvailable.
            var state = Frame(textInputAvailable: false, newKeys: new List<Keys> { Keys.LeftShift, Keys.A });
            var buffer = new StringBuilder();
            var result = Apply(state, buffer);

            Assert.True(result.ShiftDown);
            Assert.Equal("A", buffer.ToString());
        }

        [Fact]
        public void WithoutATextInputEventANavigationKeyStillReachesTheControl()
        {
            var state = Frame(textInputAvailable: false, newKeys: new List<Keys> { Keys.Left });
            var result = Apply(state, new StringBuilder());

            Assert.Equal(new[] { Keys.Left }, result.UnhandledKeys);
        }

        [Fact]
        public void TheKeymapAndTheClassifierAgreeOnWhatIsNotACharacter()
        {
            // The reason TranslateChar is still here at all after the fix: it is the test for
            // "does this key produce a character", which is what keeps navigation keys out of
            // the text buffer. It is no longer how text arrives. Swept over every Keys value, so
            // a key added to the map without a case - or a case without a map entry - fails here
            // rather than becoming a character that never gets inserted.
            //
            // numLock is false because a synthesised KeyboardState always reports it false, and
            // the numpad cases are gated on it.
            foreach (Keys key in System.Enum.GetValues(typeof(Keys)))
            {
                var producesCharacter = InputManager.TranslateChar(key, false, false, false) != '\0';
                var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { key });
                var result = Apply(state, new StringBuilder());

                if (producesCharacter)
                {
                    Assert.DoesNotContain(key, result.UnhandledKeys);
                }
                else
                {
                    // Control keys (Back, Enter, Tab, the modifiers) are consumed by the
                    // built-in actions and never reach UnhandledKeys; everything else that is
                    // not a character is navigation or an unhandled function key.
                    switch (key)
                    {
                        case Keys.None:
                        case Keys.Back:
                        case Keys.Delete:
                        case Keys.Enter:
                        case Keys.Tab:
                        case Keys.LeftShift:
                        case Keys.RightShift:
                        case Keys.LeftControl:
                        case Keys.RightControl:
                        case Keys.LeftAlt:
                        case Keys.RightAlt:
                            continue;
                        default:
                            Assert.Contains(key, result.UnhandledKeys);
                            break;
                    }
                }
            }
        }

        // ---- the actions that were never broken must stay unbroken ----------------------------------

        [Fact]
        public void BackspaceStillDeletesAndIsNotReportedAsUnhandled()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.Back });
            var buffer = new StringBuilder("abc");
            var result = Apply(state, buffer, cursor: 3);

            Assert.Equal("ab", buffer.ToString());
            Assert.Equal(1, result.NumDeletes);
            Assert.Empty(result.UnhandledKeys);
        }

        [Fact]
        public void EnterStillRaisesEnterPressedAndIsNotReportedAsUnhandled()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.Enter });
            var result = Apply(state, new StringBuilder());

            Assert.True(result.EnterPressed);
            Assert.Empty(result.UnhandledKeys);
        }

        [Fact]
        public void TabStillRaisesTabPressed()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys> { Keys.Tab });
            var result = Apply(state, new StringBuilder());

            Assert.True(result.TabPressed);
        }

        [Fact]
        public void AnEmptyFrameReturnsNullRatherThanAnEmptyResult()
        {
            var state = Frame(textInputAvailable: true, newKeys: new List<Keys>());

            Assert.Null(Apply(state, new StringBuilder()));
        }
    }
}
