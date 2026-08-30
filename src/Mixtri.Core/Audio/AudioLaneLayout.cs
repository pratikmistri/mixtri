namespace Mixtri.Core.Audio;

/// <summary>
/// One block competing for space in a stacked lane, reduced to just what packing needs.
/// </summary>
/// <param name="Id">Identity the caller uses to look the assigned row back up.</param>
/// <param name="Start">Where the block starts on the output timeline.</param>
/// <param name="End">Where it stops. Touching blocks (one ending exactly where the next
/// starts) do NOT overlap and may share a row.</param>
public readonly record struct LaneBlock(string Id, TimeSpan Start, TimeSpan End);

/// <summary>
/// Assigns overlapping timeline blocks to stacked sub-rows so none are drawn on top of
/// each other.
/// </summary>
/// <remarks>
/// <para>
/// Two music beds covering the same stretch of timeline used to render into the same band,
/// which made both waveforms unreadable and left the lower one effectively unclickable.
/// Stacking them is the standard fix, and the standard algorithm is a greedy sweep: walk the
/// blocks in start order and drop each into the FIRST row whose previous block has already
/// finished.
/// </para>
/// <para>
/// Greedy is optimal here — it uses exactly as many rows as the maximum number of blocks
/// overlapping at any one instant, which is the true lower bound (that many blocks share an
/// instant, so they cannot share rows). No amount of reordering does better, so there is
/// nothing to gain from a more elaborate strategy.
/// </para>
/// <para>
/// Pure and separated from the control because it decides what is clickable, not merely what
/// is pretty: a block assigned the wrong row is hit-tested in a band it is not drawn in.
/// </para>
/// </remarks>
public static class AudioLaneLayout
{
    /// <summary>
    /// Packs <paramref name="blocks"/> into rows, returning the row index for each block id.
    /// </summary>
    /// <param name="blocks">Blocks to pack; order is irrelevant, they are sorted internally.</param>
    /// <param name="maxRows">
    /// Ceiling on the number of rows. Blocks that would need another row past this share the
    /// last one (and so overlap again) — an unbounded stack would push the rest of the
    /// timeline off a small window, which is a worse problem than two overlapping beds.
    /// </param>
    public static Dictionary<string, int> PackIntoRows(IEnumerable<LaneBlock> blocks, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        if (maxRows < 1) maxRows = 1;

        var rowByBlock = new Dictionary<string, int>(StringComparer.Ordinal);

        // Latest end time currently occupying each row.
        var rowEnds = new List<TimeSpan>();

        foreach (var block in blocks.OrderBy(b => b.Start))
        {
            int row = -1;
            for (int r = 0; r < rowEnds.Count; r++)
            {
                // >= not >: a block starting exactly where the previous one ends does not
                // overlap it, and forcing those onto separate rows would double the height
                // of the common "clips laid end to end" case for no reason.
                if (block.Start >= rowEnds[r]) { row = r; break; }
            }

            if (row < 0)
            {
                if (rowEnds.Count < maxRows)
                {
                    rowEnds.Add(TimeSpan.Zero);
                    row = rowEnds.Count - 1;
                }
                else
                {
                    row = rowEnds.Count - 1;
                }
            }

            // Never move a row's end backwards: at the cap several blocks share the last row,
            // and a shorter one arriving later must not make that row look free again.
            if (block.End > rowEnds[row]) rowEnds[row] = block.End;

            rowByBlock[block.Id] = row;
        }

        return rowByBlock;
    }

    /// <summary>
    /// Number of rows a packing occupies — at least one, so a lane always has a band to draw
    /// in even when it is empty.
    /// </summary>
    public static int RowCount(IReadOnlyDictionary<string, int> rowByBlock)
    {
        ArgumentNullException.ThrowIfNull(rowByBlock);
        int max = 0;
        foreach (var row in rowByBlock.Values)
            if (row + 1 > max) max = row + 1;
        return Math.Max(1, max);
    }
}
