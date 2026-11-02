namespace MagSid.Core.Model;

/// <summary>
/// The Note column of an instrument's frames, as typed. A note program has no empty step, so a blank
/// frame ahead of a later note gets a note: the first note when nothing comes before it, otherwise
/// the note before it, since a frame with nothing new stays where it was. Blanks after the last note
/// stay blank, which is where the column ends.
/// </summary>
public static class InstrumentNoteColumn
{
    /// <param name="cells">One text per frame: a note or blank.</param>
    public static List<string> FillGaps(IReadOnlyList<string> cells)
    {
        var filled = cells.ToList();

        int first = filled.FindIndex(cell => cell.Length > 0);
        if (first < 0)
            return filled;

        int last = filled.FindLastIndex(cell => cell.Length > 0);

        for (int i = 0; i < first; i++)
            filled[i] = filled[first];

        for (int i = first + 1; i < last; i++)
        {
            if (filled[i].Length == 0)
                filled[i] = filled[i - 1];
        }

        return filled;
    }
}
