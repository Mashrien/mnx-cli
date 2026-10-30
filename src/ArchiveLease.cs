using Mash.IO.Format.MNX.Archive;
using ArchiveFile = Mash.IO.Format.MNX.MNX;

namespace Mash.UI.MNXFile.CLI;

/// <summary>Scope command progress handlers without closing an interactive session's archive.</summary>
internal sealed class ArchiveLease : IDisposable {
    private readonly bool _ownsArchive;
    private readonly List<EventHandler<OperationProgressArgs>> _progressHandlers = new List<EventHandler<OperationProgressArgs>>();
    public ArchiveFile Archive { get; }
    public ArchiveLease(ArchiveFile archive, bool ownsArchive) {
        Archive = archive;
        _ownsArchive = ownsArchive;
        }

    public void OnProgress(EventHandler<OperationProgressArgs> handler) {
        Archive.ProgressReport += handler;
        _progressHandlers.Add(handler);
        }

    public void Dispose() {
        foreach (EventHandler<OperationProgressArgs> handler in _progressHandlers) {
            Archive.ProgressReport -= handler;
            }

        if (_ownsArchive) {
            Archive.Dispose();
            }
        }
    }
