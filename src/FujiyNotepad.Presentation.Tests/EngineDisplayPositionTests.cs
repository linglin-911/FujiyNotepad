using FujiyNotepad.Core;
using FujiyNotepad.Presentation;

namespace FujiyNotepad.Presentation.Tests
{
    public class EngineDisplayPositionTests
    {
        private static async Task<TextLayoutEngine> NewEngineAsync(
            string content, double vw = 100, double vh = 200)
        {
            LineProvider provider = await TestData.BuildProviderAsync(content);
            var engine = new TextLayoutEngine { ViewportWidth = vw, ViewportHeight = vh };
            engine.SetMetrics(10, 20);
            engine.SetProvider(provider);
            return engine;
        }

        [Fact]
        public async Task PositionFromDisplayIndex_MapsExpandedTabSpacesToSourcePosition()
        {
            TextLayoutEngine engine = await NewEngineAsync("A\t中B");

            Assert.Equal(new TextPosition(0, 1), engine.PositionFromDisplayIndex(0, 2, trailing: false));
            Assert.Equal(new TextPosition(0, 2), engine.PositionFromDisplayIndex(0, 2, trailing: true));
        }

        [Fact]
        public async Task WrappedRows_ExposeDisplayOffsetsThatMapBackToSourcePositions()
        {
            TextLayoutEngine engine = await NewEngineAsync("ABCDEFGHIJ", vw: 60);
            engine.WordWrap = true;

            IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(new[] { "ABCD", "EFGH", "IJ" }, rows.Select(row => row.Display));
            Assert.Equal(new[] { 0, 4, 8 }, rows.Select(row => row.DisplayStartIndex));
            Assert.Equal(new TextPosition(0, 6),
                engine.PositionFromDisplayIndex(0, rows[1].DisplayStartIndex + 2, trailing: false));
        }

        [Fact]
        public async Task SelectionMetadata_UsesExpandedDisplayIndicesForTabs()
        {
            TextLayoutEngine engine = await NewEngineAsync("A\tBC");
            engine.SelectMatch(0, 1, 2); // Select the tab and B.

            VisibleLine line = engine.GetVisibleLines(hasFocus: false, caretVisible: false)[0];

            Assert.True(line.HasSelection);
            Assert.Equal(1, line.SelectionStartIndex);
            Assert.Equal(4, line.SelectionLength); // Three expanded tab spaces plus B.
        }

        [Fact]
        public async Task WrappedSelection_DoesNotPaintPreviousRowWhenStartingAtWrapBoundary()
        {
            TextLayoutEngine engine = await NewEngineAsync("ABCDEFGHIJ", vw: 60);
            engine.WordWrap = true;
            engine.SelectMatch(0, 4, 4); // Select exactly the second wrapped row.

            IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.False(rows[0].HasSelection);
            Assert.True(rows[1].HasSelection);
            Assert.Equal(0, rows[1].SelectionStartIndex);
            Assert.Equal(4, rows[1].SelectionLength);
            Assert.False(rows[1].SelectionContinuesPastRow);
            Assert.False(rows[2].HasSelection);
        }

        [Fact]
        public async Task WrappedSelection_IncludesNewlineOnAnEmptyIntermediateLine()
        {
            TextLayoutEngine engine = await NewEngineAsync("A\n\nB");
            engine.WordWrap = true;
            engine.PointerPress(new TextPosition(0, 0), shift: false);
            engine.PointerDrag(new TextPosition(2, 0));

            IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.True(rows[0].SelectionContinuesPastRow);
            Assert.True(rows[1].HasSelection);
            Assert.True(rows[1].SelectionContinuesPastRow);
            Assert.False(rows[2].HasSelection);
        }

        [Fact]
        public async Task WrappedSelectionMetadata_IsLocalToEachDisplayRow()
        {
            TextLayoutEngine engine = await NewEngineAsync("ABCDEFGHIJ", vw: 60);
            engine.WordWrap = true;
            engine.SelectMatch(0, 2, 6); // Select C through H.

            IReadOnlyList<VisibleLine> rows = engine.GetVisibleLines(hasFocus: false, caretVisible: false);

            Assert.Equal(2, rows[0].SelectionStartIndex);
            Assert.Equal(2, rows[0].SelectionLength);
            Assert.True(rows[0].SelectionContinuesPastRow);

            Assert.Equal(0, rows[1].SelectionStartIndex);
            Assert.Equal(4, rows[1].SelectionLength);
            Assert.False(rows[1].SelectionContinuesPastRow);
        }
    }
}
