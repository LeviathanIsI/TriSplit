using System;
using System.Collections.Generic;

namespace TriSplit.Desktop.Services;

public sealed class NewSourceRequestedEventArgs : EventArgs
{
    public NewSourceRequestedEventArgs(string filePath, IReadOnlyList<string> headers)
    {
        FilePath = filePath;
        Headers = headers;
    }

    public string FilePath { get; }
    public IReadOnlyList<string> Headers { get; }
}
