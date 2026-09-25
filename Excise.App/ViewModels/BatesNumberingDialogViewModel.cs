using Excise.Core.Editing;
using System;
using System.Collections.Generic;
using System.Linq;
using Excise.App.Services;
using ReactiveUI;

namespace Excise.App.ViewModels;

/// <summary>
/// ViewModel for the "Bates Numbering" dialog (#1306).
/// </summary>
/// <remarks>
/// <para>
/// Collects the stamp settings and hands back a <see cref="BatesOptions"/>;
/// it never touches a <c>PdfDocument</c>, so it is unit-testable with no open
/// document — the same decoupling <see cref="MakeSearchableDialogViewModel"/>
/// uses.
/// </para>
/// <para>
/// Bates numbering was listed in README's Desktop app feature list while
/// <c>BatesNumberingService</c> had zero production callers: no command, no
/// menu item, no CLI verb. The service and 45 tests existed; the way in did
/// not.
/// </para>
/// </remarks>
public sealed class BatesNumberingDialogViewModel : ReactiveObject
{
    /// <summary>Positions offered in the dialog, in reading order.</summary>
    public static IReadOnlyList<BatesPosition> Positions { get; } =
        Enum.GetValues<BatesPosition>().ToArray();

    private string _prefix = string.Empty;
    /// <summary>Text before the number, e.g. "DOE" for DOE000001.</summary>
    public string Prefix
    {
        get => _prefix;
        set { this.RaiseAndSetIfChanged(ref _prefix, value); RaisePreviewChanged(); }
    }

    private string _suffix = string.Empty;
    /// <summary>Text after the number, e.g. "-CONF".</summary>
    public string Suffix
    {
        get => _suffix;
        set { this.RaiseAndSetIfChanged(ref _suffix, value); RaisePreviewChanged(); }
    }

    private int _startNumber = 1;
    /// <summary>First number in the sequence.</summary>
    public int StartNumber
    {
        get => _startNumber;
        set { this.RaiseAndSetIfChanged(ref _startNumber, value); RaisePreviewChanged(); }
    }

    private int _numberOfDigits = 6;
    /// <summary>Zero-padded width of the number.</summary>
    public int NumberOfDigits
    {
        get => _numberOfDigits;
        set { this.RaiseAndSetIfChanged(ref _numberOfDigits, value); RaisePreviewChanged(); }
    }

    private BatesPosition _position = BatesPosition.BottomRight;
    /// <summary>Corner or edge the stamp sits in.</summary>
    public BatesPosition Position
    {
        get => _position;
        set => this.RaiseAndSetIfChanged(ref _position, value);
    }

    private double _fontSize = 10;
    /// <summary>Stamp size in points.</summary>
    public double FontSize
    {
        get => _fontSize;
        set => this.RaiseAndSetIfChanged(ref _fontSize, value);
    }

    /// <summary>
    /// What the first page will read, so the user can see the effect of
    /// prefix/suffix/padding before stamping anything.
    /// </summary>
    public string Preview
    {
        get
        {
            var digits = Math.Clamp(NumberOfDigits, 1, 12);
            return $"{Prefix}{Math.Max(StartNumber, 0).ToString(new string('0', digits))}{Suffix}";
        }
    }

    /// <summary>
    /// Keep the preview honest as any input changes.
    /// </summary>
    /// <remarks>
    /// Raised from each setter rather than through
    /// <c>WhenAnyValue(...)</c>: the expression-based overload is
    /// <c>RequiresUnreferencedCode</c> (IL2026) because it walks member chains
    /// by reflection, and this project keeps a zero-warning build and a
    /// trim/AOT-safe app — the same reason
    /// <see cref="MakeSearchableDialogViewModel"/> avoids the reflection-based
    /// JSON overloads.
    /// </remarks>
    private void RaisePreviewChanged() => this.RaisePropertyChanged(nameof(Preview));

    /// <summary>True when the user pressed Apply rather than Cancel.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>Record the user's confirmation. Called by the dialog.</summary>
    public void Confirm() => Confirmed = true;

    /// <summary>Translate the dialog's state into service options.</summary>
    public BatesOptions ToOptions() => new()
    {
        Prefix = Prefix ?? string.Empty,
        Suffix = Suffix ?? string.Empty,
        StartNumber = StartNumber,
        NumberOfDigits = Math.Clamp(NumberOfDigits, 1, 12),
        Position = Position,
        FontSize = FontSize <= 0 ? 10 : FontSize,
    };
}
