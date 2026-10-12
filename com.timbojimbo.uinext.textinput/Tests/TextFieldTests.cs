#if UINEXT_TEXTINPUT
using NUnit.Framework;
using TimboJimbo.UINext.TextInput;
using TimboJimbo.UINext;
using UnityEngine;
using TextField = TimboJimbo.UINext.TextField;

namespace TimboJimboTests.UINext
{
    public sealed class TextFieldTests : UITestScene
    {
        private TextField Field(bool multiline = false, int maxLines = 3)
        {
            var node = Root.Node.Child("Field").Sized(Sizing.Fixed(200f), Sizing.Fit());
            var field = node.TextField("Placeholder", 17f, multiline, maxLines);
            Frame();
            return field;
        }

        // What the keyboard does to it, as the platform's text input hands it on.
        private static ITextInputClient Keys(TextField field) => field;

        [Test]
        public void TypingGoesInAtTheCaretAndKeysMoveAndDeleteByWords()
        {
            var field = Field();
            Keys(field).InsertText("Hello");
            Keys(field).InsertText(" world");
            Assert.AreEqual("Hello world", field.Text);
            Keys(field).PerformIntent(TextEditIntent.MoveWordLeft, false);
            Assert.AreEqual(6, field.Value.Selection.Extent);
            Keys(field).InsertText("big ");
            Assert.AreEqual("Hello big world", field.Text);
            Keys(field).PerformIntent(TextEditIntent.DeleteWordBackward, false);
            Assert.AreEqual("Hello world", field.Text);
            Keys(field).PerformIntent(TextEditIntent.MoveRight, true);
            Assert.AreEqual(new TextSelection(6, 7), field.Value.Selection, "shift and an arrow selects");
        }

        [Test]
        public void SelectAllThenTypingReplacesItAndUndoBringsItBack()
        {
            var field = Field();
            field.Text = "Something";
            Keys(field).PerformIntent(TextEditIntent.SelectAll, false);
            Keys(field).InsertText("Else");
            Assert.AreEqual("Else", field.Text);
            Keys(field).PerformIntent(TextEditIntent.Undo, false);
            Assert.AreEqual("Something", field.Text);
        }

        [Test]
        public void ASingleLineTakesNoLineBreaksAndMaxLengthCutsWhatGoesIn()
        {
            var field = Field();
            field.MaxLength = 8;
            field.Insert("one\ntwo\nthree");
            Assert.AreEqual("onetwoth", field.Text);
        }

        [Test]
        public void AMultiLineFieldGrowsWithItsLinesUpToItsMost()
        {
            var field = Field(multiline: true, maxLines: 3);
            float one = field.Node.LaidOutRect.height;
            Assert.Greater(one, 10f, "one line high when empty");
            Keys(field).InsertText("one\ntwo");
            Run(1f);
            Assert.AreEqual(one * 2f, field.Node.LaidOutRect.height, 1f);
            Keys(field).InsertText("\nthree\nfour\nfive");
            Run(1f);
            Assert.AreEqual(one * 3f, field.Node.LaidOutRect.height, 1f, "three lines at most, then it scrolls");
        }

        [Test]
        public void ItDrawsItsPlaceholderWhileEmptyAndItsTextOnceTyped()
        {
            var field = Field();
            var text = field.TextNode;
            Assert.Greater(text.Instances.Count, 0, "the placeholder");
            int placeholder = text.Instances.Count;
            Keys(field).InsertText("Hi");
            Frame();
            Assert.Greater(text.Instances.Count, 0);
            Assert.AreNotEqual(placeholder, text.Instances.Count, "two letters, not eleven");
        }
    }
}
#endif
