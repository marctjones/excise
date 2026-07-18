using System;
using ReactiveUI;

namespace Excise.App.Models;

public class ClipboardEntry : ReactiveObject
{
    private string _text = string.Empty;
    private DateTime _timestamp;
    private int _pageNumber;
    private bool _isRedacted;

    public string Text
    {
        get => _text;
        set
        {
            this.RaiseAndSetIfChanged(ref _text, value);
            this.RaisePropertyChanged(nameof(PreviewText));
            this.RaisePropertyChanged(nameof(CharacterCount));
        }
    }

    public DateTime Timestamp
    {
        get => _timestamp;
        set
        {
            this.RaiseAndSetIfChanged(ref _timestamp, value);
            this.RaisePropertyChanged(nameof(TimeDisplay));
        }
    }

    public int PageNumber
    {
        get => _pageNumber;
        set => this.RaiseAndSetIfChanged(ref _pageNumber, value);
    }

    public bool IsRedacted
    {
        get => _isRedacted;
        set
        {
            this.RaiseAndSetIfChanged(ref _isRedacted, value);
            this.RaisePropertyChanged(nameof(TypeIndicator));
        }
    }

    // Computed properties for UI display
    public string TypeIndicator => IsRedacted ? "REDACTED" : "Copied";
    public string TimeDisplay
    {
        get
        {
            var now = DateTime.Now;
            var diff = now - Timestamp;

            if (diff.TotalMinutes < 1)
                return "Just now";
            if (diff.TotalMinutes < 60)
                return $"{(int)diff.TotalMinutes}m ago";
            if (diff.TotalHours < 24)
                return $"{(int)diff.TotalHours}h ago";
            if (diff.TotalDays < 7)
                return $"{(int)diff.TotalDays}d ago";

            return Timestamp.ToString("MMM d, h:mm tt");
        }
    }

    public string PreviewText
    {
        get
        {
            if (string.IsNullOrEmpty(Text))
                return string.Empty;

            // Show first 100 characters as preview
            const int maxLength = 100;
            if (Text.Length <= maxLength)
                return Text;

            return Text.Substring(0, maxLength) + "...";
        }
    }

    public int CharacterCount => Text?.Length ?? 0;
}
