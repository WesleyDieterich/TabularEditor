using System.Drawing;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TabularEditor.TextServices;
using TabularEditor.UI;

namespace TabularEditor
{
    [TestClass]
    public class ColorLiteralsTests
    {
        private static string[] Find(string text) =>
            ColorLiterals.Find(text, 0, text.Length).Select(m => text.Substring(m.Start, m.Length)).ToArray();

        [TestMethod]
        public void FindsColorsOfEveryLength()
        {
            CollectionAssert.AreEqual(new[] { "#2B8A3E" }, Find("\"#2B8A3E\""));
            CollectionAssert.AreEqual(new[] { "#abc" }, Find("\"#abc\""));
            CollectionAssert.AreEqual(new[] { "#2B8A3E80" }, Find("\"#2B8A3E80\""));
        }

        [TestMethod]
        public void FindsColorsInsideLongerStrings()
        {
            CollectionAssert.AreEqual(new[] { "#2B8A3E", "#FFF" },
                Find("\"<rect fill='#2B8A3E' stroke=\"\"#FFF\"\"/>\""));
        }

        [TestMethod]
        public void IgnoresThingsThatAreNotColors()
        {
            CollectionAssert.AreEqual(new string[0], Find("\"#12345\""));      // 5 digits
            CollectionAssert.AreEqual(new string[0], Find("\"#2B8A3EZ\""));    // runs into a letter
            CollectionAssert.AreEqual(new string[0], Find("\"&#123;\""));      // HTML entity
            CollectionAssert.AreEqual(new string[0], Find("\"Item#123\""));    // part of a word
            CollectionAssert.AreEqual(new string[0], Find("\"#\""));
        }

        [TestMethod]
        public void FindStaysWithinTheGivenSpan()
        {
            var text = "#FFF \"#000\" #FFF";
            var matches = ColorLiterals.Find(text, 5, 6).ToArray();
            Assert.AreEqual(1, matches.Length);
            Assert.AreEqual(6, matches[0].Start);
        }

        [TestMethod]
        public void ParsesColors()
        {
            Assert.IsTrue(ColorLiterals.TryParse("#2B8A3E", out var color));
            Assert.AreEqual(Color.FromArgb(255, 0x2B, 0x8A, 0x3E), color);
            Assert.IsTrue(ColorLiterals.TryParse("#f0a", out color));
            Assert.AreEqual(Color.FromArgb(255, 0xFF, 0x00, 0xAA), color);
            Assert.IsTrue(ColorLiterals.TryParse("#2B8A3E80", out color));
            Assert.AreEqual(Color.FromArgb(0x80, 0x2B, 0x8A, 0x3E), color);
            Assert.IsFalse(ColorLiterals.TryParse("2B8A3E", out _));
            Assert.IsFalse(ColorLiterals.TryParse("#2B8A3", out _));
            Assert.IsFalse(ColorLiterals.TryParse("#GGGGGG", out _));
        }

        [TestMethod]
        public void FormatKeepsTheOriginalStyle()
        {
            var green = Color.FromArgb(0x2B, 0x8A, 0x3E);
            Assert.AreEqual("#2B8A3E", ColorLiterals.Format(green, "#D9480F"));
            Assert.AreEqual("#2b8a3e", ColorLiterals.Format(green, "#d9480f"));
            Assert.AreEqual("#2B8A3E", ColorLiterals.Format(green, "#123456"), "digits only: upper case");
            Assert.AreEqual("#2B8A3EFF", ColorLiterals.Format(green, "#D9480F80"));
            Assert.AreEqual("#2B8A3E", ColorLiterals.Format(green, "#FFF"), "3 digits widen when needed");
            Assert.AreEqual("#F0A", ColorLiterals.Format(Color.FromArgb(0xFF, 0x00, 0xAA), "#FFF"));
            Assert.AreEqual("#f0a", ColorLiterals.Format(Color.FromArgb(0xFF, 0x00, 0xAA), "#fff"));
        }

        [TestMethod]
        public void HsvRoundTrips()
        {
            foreach (var color in new[] { Color.FromArgb(0x2B, 0x8A, 0x3E), Color.FromArgb(0xD9, 0x48, 0x0F),
                Color.FromArgb(0x1C, 0x7E, 0xD6), Color.Black, Color.White, Color.FromArgb(0xAD, 0xB5, 0xBD), Color.Magenta })
            {
                ColorPickerPopup.ToHsv(color, out var h, out var s, out var v);
                Assert.AreEqual(color.ToArgb(), ColorPickerPopup.FromHsv(h, s, v, 255).ToArgb(), color.ToString());
            }
            Assert.AreEqual(Color.Red.ToArgb(), ColorPickerPopup.FromHsv(360, 1, 1, 255).ToArgb());
        }

        [TestMethod]
        public void ChipTextContrast()
        {
            Assert.IsTrue(ColorLiterals.IsLight(Color.FromArgb(0xAD, 0xB5, 0xBD)));
            Assert.IsTrue(ColorLiterals.IsLight(Color.FromArgb(0xF5, 0x9F, 0x00)));
            Assert.IsFalse(ColorLiterals.IsLight(Color.FromArgb(0x1E, 0x3A, 0x8A)));
            Assert.IsFalse(ColorLiterals.IsLight(Color.Black));
            Assert.IsTrue(ColorLiterals.IsLight(Color.FromArgb(0, Color.Black)), "fully transparent shows the light checkers");
        }
    }
}
