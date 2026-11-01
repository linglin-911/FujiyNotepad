using FujiyNotepad.Core;

namespace FujiyNotepad.Core.Tests
{
    public class LineColumnsMappingTests
    {
        [Fact]
        public void DisplayIndexMapping_MapsExpandedTabSpacesToTheOriginalTab()
        {
            LineColumns columns = LineColumns.Build("A\t中B", tabSize: 4);

            Assert.Equal("A   中B", columns.Display);
            Assert.Equal(1, columns.DisplayIndexOfCharIndex(1));
            Assert.Equal(4, columns.DisplayIndexOfCharIndex(2));

            // Any expanded tab space maps to the tab before it, or to the character after it when trailing.
            Assert.Equal(1, columns.CharIndexOfDisplayIndex(2, trailing: false));
            Assert.Equal(2, columns.CharIndexOfDisplayIndex(2, trailing: true));
            Assert.Equal(2, columns.CharIndexOfDisplayIndex(4, trailing: false));
            Assert.Equal(3, columns.CharIndexOfDisplayIndex(4, trailing: true));
        }

        [Fact]
        public void DisplayIndexMapping_PreservesSupplementaryUnicodeCharacterAsOneCaretStep()
        {
            LineColumns columns = LineColumns.Build("A😀B", tabSize: 4);

            // Win2D reports the two UTF-16 code units of the emoji as one character cluster.
            Assert.Equal(3, columns.CharIndexOfDisplayIndex(1, trailing: true, displayCharacterCount: 2));
        }
    }
}
