using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Threading;

namespace SupraChat;

/// <summary>Keep the owner and dispatcher alive until its metadata review has finalized.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeReviewOwnerLifetime
{
    internal bool IsClosing { get; private set; }
    private readonly Window _owner;
    private bool _allowClose;
    internal static NativeReviewOwnerLifetime Attach(Window owner, Func<NativeMetadataReviewWindow?> currentReview) => new(owner, currentReview);
    private NativeReviewOwnerLifetime(Window owner, Func<NativeMetadataReviewWindow?> currentReview)
    {
        _owner = owner;
        owner.Closing += (_, args) =>
        {
            if (_allowClose) return;
            var review = currentReview();
            if (!IsClosing && review is null) return;
            args.Cancel = true;
            Begin(review!, requestClose: true);
        };
    }
    internal void TrackReview(NativeMetadataReviewWindow review)
    {
        // Avalonia raises child Closing first. Its cancellation can suppress owner Closing.
        review.Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.OwnerWindowClosing) Begin(review, requestClose: false);
        };
    }
    private void Begin(NativeMetadataReviewWindow review, bool requestClose)
    {
        if (IsClosing) return;
        IsClosing = true;
        if (requestClose) review.Close();
        _ = Finish();
        async Task Finish()
        {
            await review.CloseCompletion;
            _allowClose = true;
            Dispatcher.UIThread.Post(_owner.Close);
        }
    }
}
