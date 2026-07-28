using System;

namespace Excise.App.ViewModels;

/// <summary>
/// Canonical, verbatim license texts keyed by SPDX id (#831 follow-up to #823).
///
/// Some packages declare their license as an SPDX *expression* (e.g. <c>MIT</c>)
/// and ship no license *file* on NuGet, so the manifest carries an id but no
/// verbatim <c>licenseText</c>. Permissive licenses like MIT/BSD nonetheless
/// require the permission-notice text to travel with the redistribution — the
/// copyright line alone is not enough. This table supplies the standard body so
/// the About dialog can show the full text for EVERY shipped package, not only
/// those that happened to bundle a file.
///
/// The texts are the canonical SPDX license bodies. Where a license carries a
/// per-package copyright line, callers pass the package's own copyright so the
/// rendered notice is correct for that package.
/// </summary>
internal static class SpdxLicenseTexts
{
    /// <summary>
    /// The verbatim license body for <paramref name="spdx"/>, or null if we do
    /// not carry canonical text for that id. <paramref name="copyright"/> — the
    /// package's own copyright notice — is woven into licenses that have a
    /// per-package copyright line (MIT, BSD); a null/blank copyright falls back
    /// to a generic holder so the notice is still complete.
    /// </summary>
    public static string? ForSpdx(string? spdx, string? copyright)
    {
        if (string.IsNullOrWhiteSpace(spdx)) return null;

        var holder = string.IsNullOrWhiteSpace(copyright)
            ? "the copyright holders"
            : copyright!.Trim();

        return spdx!.Trim() switch
        {
            "MIT" => Mit(holder),
            "BSD-2-Clause" => Bsd2(holder),
            "BSD-3-Clause" => Bsd3(holder),
            "0BSD" => ZeroBsd(holder),
            _ => null,
        };
    }

    /// <summary>True when a full verbatim body is available for this SPDX id.</summary>
    public static bool HasText(string? spdx) => ForSpdx(spdx, null) != null;

    private static string Mit(string holder) =>
$@"MIT License

{CopyrightLine(holder)}

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the ""Software""), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED ""AS IS"", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.";

    private static string Bsd2(string holder) =>
$@"BSD 2-Clause License

{CopyrightLine(holder)}

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS ""AS IS""
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.";

    private static string Bsd3(string holder) =>
$@"BSD 3-Clause License

{CopyrightLine(holder)}

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its contributors
   may be used to endorse or promote products derived from this software
   without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS ""AS IS""
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.";

    private static string ZeroBsd(string holder) =>
$@"BSD Zero Clause License

{CopyrightLine(holder)}

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted.

THE SOFTWARE IS PROVIDED ""AS IS"" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY
AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM
LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR
OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR
PERFORMANCE OF THIS SOFTWARE.";

    private static string CopyrightLine(string holder) =>
        holder.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)
            ? holder
            : $"Copyright (c) {holder}";
}
