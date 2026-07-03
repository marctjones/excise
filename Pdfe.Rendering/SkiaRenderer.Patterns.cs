using Pdfe.Core.ColorSpaces;
using Pdfe.Core.Content;
using Pdfe.Core.Document;
using Pdfe.Core.Primitives;
using Pdfe.Rendering.Shadings;
using SkiaSharp;

namespace Pdfe.Rendering;

internal partial class RenderContext
{
    #region Shading (sh operator) - Issue #300

    private bool RenderFillPattern(SKPath path)
    {
        if (_state.FillPatternName == null)
            return false;

        var pattern = ResolvePatternFromActiveResources(_state.FillPatternName);
        if (pattern == null)
            return false;

        if (pattern.GetInt("PatternType", 0) == 1)
            return RenderTilingPattern(path, pattern);

        if (pattern.GetInt("PatternType", 0) != 2)
            return false;

        var shadingObj = pattern.GetOptional("Shading");
        if (shadingObj == null)
            return false;
        var shading = _page.Document.Resolve(shadingObj) as Pdfe.Core.Primitives.PdfDictionary;
        if (shading == null)
            return false;

        return shading.GetInt("ShadingType", 0) switch
        {
            1 or 2 or 3 => RenderShadingPattern(path, pattern, shading),
            4 => RenderType4MeshPattern(path, pattern, shading),
            6 => RenderType6MeshPattern(path, pattern, shading),
            _ => false
        };
    }

    private bool RenderTilingPattern(SKPath clipPath, Pdfe.Core.Primitives.PdfDictionary pattern)
    {
        if (pattern is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        var content = stream.DecodedData;
        if (content.Length == 0)
            return false;

        var bboxArray = pattern.GetOptional("BBox") as Pdfe.Core.Primitives.PdfArray;
        if (bboxArray == null || bboxArray.Count < 4)
            return false;

        var bbox = new SKRect(
            (float)Math.Min(ArrayNumberOrDefault(bboxArray, 0), ArrayNumberOrDefault(bboxArray, 2)),
            (float)Math.Min(ArrayNumberOrDefault(bboxArray, 1), ArrayNumberOrDefault(bboxArray, 3)),
            (float)Math.Max(ArrayNumberOrDefault(bboxArray, 0), ArrayNumberOrDefault(bboxArray, 2)),
            (float)Math.Max(ArrayNumberOrDefault(bboxArray, 1), ArrayNumberOrDefault(bboxArray, 3)));
        if (bbox.Width <= 0 || bbox.Height <= 0)
            return false;

        var xStep = (float)pattern.GetNumber("XStep", bbox.Width);
        var yStep = (float)pattern.GetNumber("YStep", bbox.Height);
        if (Math.Abs(xStep) < 0.001f || Math.Abs(yStep) < 0.001f)
            return false;

        var paintType = pattern.GetInt("PaintType", 1);
        var patternResources = pattern.GetOptional("Resources") is { } resObj
            ? _page.Document.Resolve(resObj) as Pdfe.Core.Primitives.PdfDictionary
            : null;
        var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
        var inverseCtm = InvertAffine(_state.CurrentTransform);
        if (!inverseCtm.HasValue)
            return false;

        _canvas.Save();
        _resourcesStack.Push(patternResources);
        var savedState = _state.Clone();
        try
        {
            _tilingPatternDepth++;
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inv = inverseCtm.Value;
            _canvas.Concat(in inv);
            _canvas.Concat(in patternMatrix);
            _state.FillPatternName = null;
            if (paintType == 2)
            {
                _state.StrokeColor = savedState.FillColor;
                _state.FillColor = savedState.FillColor;
                _state.StrokeAlpha = savedState.FillAlpha;
                _state.FillAlpha = savedState.FillAlpha;
            }

            var clip = _canvas.LocalClipBounds;
            if (clip.Width <= 0 || clip.Height <= 0)
                return false;

            var xStepAbs = Math.Abs(xStep);
            var yStepAbs = Math.Abs(yStep);
            if (NeedsComposedTilingCell(bbox, xStepAbs, yStepAbs))
                return RenderComposedTilingPatternCells(content, clip, bbox, xStepAbs, yStepAbs);

            var tileMinX = (float)(Math.Ceiling((clip.Left - bbox.Right) / xStepAbs) * xStepAbs);
            var tileMaxX = (float)(Math.Floor((clip.Right - bbox.Left) / xStepAbs) * xStepAbs);
            var tileMinY = (float)(Math.Ceiling((clip.Top - bbox.Bottom) / yStepAbs) * yStepAbs);
            var tileMaxY = (float)(Math.Floor((clip.Bottom - bbox.Top) / yStepAbs) * yStepAbs);
            if (tileMinX > tileMaxX || tileMinY > tileMaxY)
                return true;

            const int maxTiles = 4096;
            var tileCount = 0;
            for (var ty = tileMinY; ty <= tileMaxY; ty += yStepAbs)
            {
                for (var tx = tileMinX; tx <= tileMaxX; tx += xStepAbs)
                {
                    if (++tileCount > maxTiles)
                        return false;

                    RenderTilingPatternContentInstance(content, tx, ty, bbox);
                }
            }

            return true;
        }
        finally
        {
            _tilingPatternDepth--;
            _state = savedState;
            _resourcesStack.Pop();
            _canvas.Restore();
        }
    }

    private bool RenderComposedTilingPatternCells(
        byte[] content,
        SKRect clip,
        SKRect bbox,
        float xStep,
        float yStep)
    {
        const float epsilon = 0.0001f;
        var cellMinX = (float)(Math.Floor(clip.Left / xStep) * xStep);
        var cellMaxX = (float)(Math.Floor((clip.Right - epsilon) / xStep) * xStep);
        var cellMinY = (float)(Math.Floor(clip.Top / yStep) * yStep);
        var cellMaxY = (float)(Math.Floor((clip.Bottom - epsilon) / yStep) * yStep);
        if (cellMinX > cellMaxX || cellMinY > cellMaxY)
            return true;

        var cellBounds = new SKRect(0, 0, xStep, yStep);

        var contributionMinX = (float)(Math.Ceiling((0 - bbox.Right + epsilon) / xStep) * xStep);
        var contributionMaxX = (float)(Math.Floor((xStep - bbox.Left - epsilon) / xStep) * xStep);
        var contributionMinY = (float)(Math.Ceiling((0 - bbox.Bottom + epsilon) / yStep) * yStep);
        var contributionMaxY = (float)(Math.Floor((yStep - bbox.Top - epsilon) / yStep) * yStep);
        if (contributionMinX > contributionMaxX || contributionMinY > contributionMaxY)
            return true;

        const int maxCellContentInstances = 4096;
        var origins = new List<SKPoint>();
        for (var relY = contributionMinY; relY <= contributionMaxY + epsilon; relY += yStep)
        {
            for (var relX = contributionMinX; relX <= contributionMaxX + epsilon; relX += xStep)
            {
                if (origins.Count >= maxCellContentInstances)
                    return false;

                var tileBounds = new SKRect(
                    relX + bbox.Left,
                    relY + bbox.Top,
                    relX + bbox.Right,
                    relY + bbox.Bottom);
                if (tileBounds.IntersectsWith(cellBounds))
                    origins.Add(new SKPoint(relX, relY));
            }
        }

        if (origins.Count == 0)
            return true;

        var cellCountX = 1 + (long)Math.Floor((cellMaxX - cellMinX) / xStep);
        var cellCountY = 1 + (long)Math.Floor((cellMaxY - cellMinY) / yStep);
        const long maxDirectContentInstances = 8192;
        if (cellCountX > 0 &&
            cellCountY > 0 &&
            cellCountX * cellCountY * origins.Count <= maxDirectContentInstances)
        {
            return RenderDirectComposedTilingPatternCells(
                content,
                clip,
                bbox,
                xStep,
                yStep,
                origins,
                cellMinX,
                cellMaxX,
                cellMinY,
                cellMaxY);
        }

        return RenderRepeatedComposedTilingPatternCell(content, clip, bbox, cellBounds, origins);
    }

    private bool RenderDirectComposedTilingPatternCells(
        byte[] content,
        SKRect clip,
        SKRect bbox,
        float xStep,
        float yStep,
        IReadOnlyList<SKPoint> origins,
        float cellMinX,
        float cellMaxX,
        float cellMinY,
        float cellMaxY)
    {
        const float epsilon = 0.0001f;
        for (var cellY = cellMinY; cellY <= cellMaxY + epsilon; cellY += yStep)
        {
            for (var cellX = cellMinX; cellX <= cellMaxX + epsilon; cellX += xStep)
            {
                var cell = new SKRect(cellX, cellY, cellX + xStep, cellY + yStep);
                if (!cell.IntersectsWith(clip))
                    continue;

                _canvas.Save();
                try
                {
                    // Pattern cell and BBox clips are lattice boundaries, not painted edges.
                    // Antialiased clipping here creates repeat seams on thin pattern strokes.
                    _canvas.ClipRect(cell, SKClipOperation.Intersect, antialias: false);

                    foreach (var origin in origins)
                        RenderTilingPatternContentInstance(content, cellX + origin.X, cellY + origin.Y, bbox);
                }
                finally
                {
                    _canvas.Restore();
                }
            }
        }

        return true;
    }

    private bool RenderRepeatedComposedTilingPatternCell(
        byte[] content,
        SKRect clip,
        SKRect bbox,
        SKRect cellBounds,
        IReadOnlyList<SKPoint> origins)
    {
        using var recorder = new SKPictureRecorder();
        var cellCanvas = recorder.BeginRecording(cellBounds);
        RenderContext? child = null;
        SKPicture? cellPicture = null;
        var recordingEnded = false;
        try
        {
            cellCanvas.Save();
            cellCanvas.ClipRect(cellBounds, SKClipOperation.Intersect, antialias: false);

            child = new RenderContext(cellCanvas, _page, _options, _cancellationToken);
            CopyRenderScopeTo(child);
            child._state = _state.Clone();
            child._state.FillPatternName = null;
            child._tilingPatternDepth = _tilingPatternDepth;

            foreach (var origin in origins)
                child.RenderTilingPatternContentInstance(content, origin.X, origin.Y, bbox);

            cellCanvas.Restore();
            cellPicture = recorder.EndRecording();
            recordingEnded = true;
            if (cellPicture == null)
                return false;

            using var shader = SKShader.CreatePicture(
                cellPicture,
                SKShaderTileMode.Repeat,
                SKShaderTileMode.Repeat,
                SKFilterMode.Nearest,
                cellBounds);
            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = _options.AntiAlias
            };

            _canvas.DrawRect(clip, paint);
            return true;
        }
        finally
        {
            if (!recordingEnded)
                recorder.EndRecording()?.Dispose();
            child?._resourcesStack.Clear();
            child?._optionalContentVisibilityStack.Clear();
            child?.DisposeOwnedResources();
            cellPicture?.Dispose();
        }
    }

    private void CopyRenderScopeTo(RenderContext child)
    {
        foreach (var resources in _resourcesStack.Reverse())
            child._resourcesStack.Push(resources);
        foreach (var visible in _optionalContentVisibilityStack.Reverse())
            child._optionalContentVisibilityStack.Push(visible);
        child._hiddenOptionalContentDepth = _hiddenOptionalContentDepth;
    }

    private void RenderTilingPatternContentInstance(byte[] content, float tx, float ty, SKRect bbox)
    {
        var savedCanvasCount = _canvas.SaveCount;
        var savedStateStack = SnapshotGraphicsStateStack();
        var savedPath = _currentPath;
        var savedPendingClip = _pendingClipEvenOdd;
        var savedPendingTextClipPath = _pendingTextClipPath;
        var savedState = _state.Clone();
        var savedTextState = _textState.Clone();
        var savedFontState = SnapshotCurrentFontState();
        var savedInTextBlock = _inTextBlock;
        _currentPath = null;
        _pendingClipEvenOdd = null;
        _pendingTextClipPath = null;
        _canvas.Save();
        try
        {
            _canvas.Translate(tx, ty);
            // Keep BBox clipping hard for the same reason as the repeat-cell clip above.
            _canvas.ClipRect(bbox, SKClipOperation.Intersect, antialias: false);
            ExecuteContentBytes(content);
        }
        finally
        {
            _currentPath?.Dispose();
            _pendingTextClipPath?.Dispose();
            RestoreGraphicsStateStack(savedStateStack);
            _currentPath = savedPath;
            _pendingClipEvenOdd = savedPendingClip;
            _pendingTextClipPath = savedPendingTextClipPath;
            _state = savedState;
            _textState = savedTextState;
            RestoreCurrentFontState(savedFontState);
            _inTextBlock = savedInTextBlock;
            _canvas.RestoreToCount(savedCanvasCount);
        }
    }

    private static bool NeedsComposedTilingCell(SKRect bbox, float xStep, float yStep)
    {
        const float epsilon = 0.0001f;
        return Math.Abs(bbox.Left) > epsilon
            || Math.Abs(bbox.Top) > epsilon
            || Math.Abs(bbox.Right - xStep) > epsilon
            || Math.Abs(bbox.Bottom - yStep) > epsilon;
    }

    private bool RenderShadingPattern(
        SKPath clipPath,
        Pdfe.Core.Primitives.PdfDictionary pattern,
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        _canvas.Save();
        try
        {
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inverseCtm = InvertAffine(_state.CurrentTransform);
            if (inverseCtm.HasValue)
            {
                var inv = inverseCtm.Value;
                _canvas.Concat(in inv);
            }

            var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
            _canvas.Concat(in patternMatrix);

            switch (shading.GetInt("ShadingType", 0))
            {
                case 1:
                    RenderFunctionShading(shading);
                    return true;
                case 2:
                    RenderAxialShading(shading);
                    return true;
                case 3:
                    RenderRadialShading(shading);
                    return true;
                default:
                    return false;
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private bool RenderType6MeshPattern(
        SKPath clipPath,
        Pdfe.Core.Primitives.PdfDictionary pattern,
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        if (shading is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        var patches = DecodeType6MeshPatches(stream, tensorPatch: false);
        if (patches.Count == 0)
            return false;

        _canvas.Save();
        try
        {
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inverseCtm = InvertAffine(_state.CurrentTransform);
            if (inverseCtm.HasValue)
            {
                var inv = inverseCtm.Value;
                _canvas.Concat(in inv);
            }

            var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
            _canvas.Concat(in patternMatrix);
            DrawMeshPatches(patches, tensorPatch: false);
        }
        finally
        {
            _canvas.Restore();
        }

        return true;
    }

    private bool RenderType4MeshPattern(
        SKPath clipPath,
        Pdfe.Core.Primitives.PdfDictionary pattern,
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        if (shading is not Pdfe.Core.Primitives.PdfStream stream)
            return false;

        var triangles = DecodeType4MeshTriangles(stream);
        if (triangles.Count == 0)
            return false;

        _canvas.Save();
        try
        {
            _canvas.ClipPath(clipPath, SKClipOperation.Intersect, _options.AntiAlias);

            var inverseCtm = InvertAffine(_state.CurrentTransform);
            if (inverseCtm.HasValue)
            {
                var inv = inverseCtm.Value;
                _canvas.Concat(in inv);
            }

            var patternMatrix = GetMatrix(pattern.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
            _canvas.Concat(in patternMatrix);
            DrawMeshTriangles(triangles);
        }
        finally
        {
            _canvas.Restore();
        }

        return true;
    }

    private List<MeshTriangle> DecodeType4MeshTriangles(Pdfe.Core.Primitives.PdfStream stream)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var xMin = decode?.Count >= 2 ? decode.GetNumber(0) : 0;
        var xMax = decode?.Count >= 2 ? decode.GetNumber(1) : 1;
        var yMin = decode?.Count >= 4 ? decode.GetNumber(2) : 0;
        var yMax = decode?.Count >= 4 ? decode.GetNumber(3) : 1;
        var bitsPerCoordinate = stream.GetInt("BitsPerCoordinate", 16);
        var bitsPerComponent = stream.GetInt("BitsPerComponent", 8);
        var bitsPerFlag = stream.GetInt("BitsPerFlag", 2);
        var functionObj = stream.GetOptional("Function");
        var function = functionObj != null ? _page.Document.Resolve(functionObj) : null;
        var colorSpace = ResolveShadingColorSpace(stream);
        var componentCount = GetMeshComponentCount(stream, colorSpace, function);

        var reader = new MeshBitReader(stream.DecodedData);
        var triangles = new List<MeshTriangle>();
        var pending = new List<MeshVertex>(3);
        MeshTriangle? previous = null;

        while (reader.RemainingBits >= bitsPerFlag + (2 * bitsPerCoordinate) + (componentCount * bitsPerComponent))
        {
            MeshVertex vertex;
            try
            {
                var flag = (int)(reader.Read(bitsPerFlag) & 0x3);
                var x = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, xMin, xMax);
                var y = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, yMin, yMax);
                var components = new double[componentCount];
                for (int i = 0; i < componentCount; i++)
                {
                    var cMin = decode?.Count >= 6 + (2 * i) ? decode.GetNumber(4 + (2 * i)) : 0;
                    var cMax = decode?.Count >= 6 + (2 * i) ? decode.GetNumber(5 + (2 * i)) : 1;
                    components[i] = Decode(reader.Read(bitsPerComponent), bitsPerComponent, cMin, cMax);
                }

                var color = ComponentsToSkColor(
                    function != null
                        ? PdfFunctionEvaluator.Evaluate(function, components[0], _page.Document) ?? new[] { components[0] }
                        : components,
                    colorSpace);
                vertex = new MeshVertex(flag, new SKPoint((float)x, (float)y), color);
            }
            catch
            {
                break;
            }

            if (vertex.Flag == 0 || previous == null)
            {
                pending.Add(vertex);
                if (pending.Count < 3)
                    continue;

                previous = new MeshTriangle(pending[0], pending[1], pending[2]);
                triangles.Add(previous);
                pending.Clear();
                continue;
            }

            pending.Clear();
            previous = vertex.Flag switch
            {
                1 => new MeshTriangle(previous.B, previous.C, vertex),
                2 => new MeshTriangle(previous.A, previous.C, vertex),
                _ => null
            };

            if (previous != null)
                triangles.Add(previous);
        }

        return triangles;
    }

    private List<MeshTriangle> DecodeType5MeshTriangles(Pdfe.Core.Primitives.PdfStream stream)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var xMin = decode?.Count >= 2 ? decode.GetNumber(0) : 0;
        var xMax = decode?.Count >= 2 ? decode.GetNumber(1) : 1;
        var yMin = decode?.Count >= 4 ? decode.GetNumber(2) : 0;
        var yMax = decode?.Count >= 4 ? decode.GetNumber(3) : 1;
        var bitsPerCoordinate = stream.GetInt("BitsPerCoordinate", 16);
        var bitsPerComponent = stream.GetInt("BitsPerComponent", 8);
        var verticesPerRow = stream.GetInt("VerticesPerRow", 0);
        if (verticesPerRow < 2)
            return new List<MeshTriangle>();

        var functionObj = stream.GetOptional("Function");
        var function = functionObj != null ? _page.Document.Resolve(functionObj) : null;
        var colorSpace = ResolveShadingColorSpace(stream);
        var componentCount = GetMeshComponentCount(stream, colorSpace, function);
        var bitsPerVertex = (2 * bitsPerCoordinate) + (componentCount * bitsPerComponent);

        var reader = new MeshBitReader(stream.DecodedData);
        var vertices = new List<MeshVertex>();
        while (reader.RemainingBits >= bitsPerVertex)
        {
            try
            {
                var x = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, xMin, xMax);
                var y = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, yMin, yMax);
                var components = ReadMeshComponents(reader, bitsPerComponent, componentCount, decode);
                var color = ComponentsToSkColor(
                    function != null
                        ? PdfFunctionEvaluator.Evaluate(function, components[0], _page.Document) ?? new[] { components[0] }
                        : components,
                    colorSpace);
                vertices.Add(new MeshVertex(0, new SKPoint((float)x, (float)y), color));
            }
            catch
            {
                break;
            }
        }

        var rowCount = vertices.Count / verticesPerRow;
        var triangles = new List<MeshTriangle>();
        for (var row = 0; row < rowCount - 1; row++)
        {
            var rowOffset = row * verticesPerRow;
            var nextRowOffset = (row + 1) * verticesPerRow;
            for (var col = 0; col < verticesPerRow - 1; col++)
            {
                var v00 = vertices[rowOffset + col];
                var v10 = vertices[rowOffset + col + 1];
                var v01 = vertices[nextRowOffset + col];
                var v11 = vertices[nextRowOffset + col + 1];
                triangles.Add(new MeshTriangle(v00, v10, v01));
                triangles.Add(new MeshTriangle(v10, v11, v01));
            }
        }

        return triangles;
    }

    private List<MeshPatch> DecodeType6MeshPatches(Pdfe.Core.Primitives.PdfStream stream, bool tensorPatch)
    {
        var decode = stream.GetOptional("Decode") as Pdfe.Core.Primitives.PdfArray;
        var xMin = decode?.Count >= 2 ? decode.GetNumber(0) : 0;
        var xMax = decode?.Count >= 2 ? decode.GetNumber(1) : 1;
        var yMin = decode?.Count >= 4 ? decode.GetNumber(2) : 0;
        var yMax = decode?.Count >= 4 ? decode.GetNumber(3) : 1;
        var bitsPerCoordinate = stream.GetInt("BitsPerCoordinate", 16);
        var bitsPerComponent = stream.GetInt("BitsPerComponent", 8);
        var bitsPerFlag = stream.GetInt("BitsPerFlag", 2);
        var functionObj = stream.GetOptional("Function");
        var function = functionObj != null ? _page.Document.Resolve(functionObj) : null;
        var colorSpace = ResolveShadingColorSpace(stream);
        var componentCount = GetMeshComponentCount(stream, colorSpace, function);

        var reader = new MeshBitReader(stream.DecodedData);
        var patches = new List<MeshPatch>();
        MeshPatch? previous = null;

        while (reader.RemainingBits >= bitsPerFlag + (8 * bitsPerCoordinate))
        {
            int flag;
            try
            {
                flag = (int)(reader.Read(bitsPerFlag) & 0x3);
            }
            catch
            {
                break;
            }

            var coordinateCount = tensorPatch
                ? flag == 0 ? 16 : 12
                : flag == 0 ? 12 : 8;
            var colorCount = flag == 0 ? 4 : 2;
            var points = new List<SKPoint>(coordinateCount);
            var colors = new List<SKColor>(colorCount);

            try
            {
                for (int i = 0; i < coordinateCount; i++)
                {
                    var x = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, xMin, xMax);
                    var y = Decode(reader.Read(bitsPerCoordinate), bitsPerCoordinate, yMin, yMax);
                    points.Add(new SKPoint((float)x, (float)y));
                }

                for (int i = 0; i < colorCount; i++)
                {
                    var components = ReadMeshComponents(reader, bitsPerComponent, componentCount, decode);
                    colors.Add(ComponentsToSkColor(
                        function != null
                            ? PdfFunctionEvaluator.Evaluate(function, components[0], _page.Document) ?? new[] { components[0] }
                            : components,
                        colorSpace));
                }
            }
            catch
            {
                break;
            }

            var patchPoints = PdfMeshPatchBuilder.ResolveCanonicalPatchPoints(points, previous, flag, tensorPatch);
            var patch = MeshPatch.From(
                patchPoints,
                PdfMeshPatchBuilder.ResolveCanonicalPatchColors(colors, previous, flag));
            patches.Add(patch);
            previous = patch;
        }

        return patches;
    }

    private static double[] ReadMeshComponents(
        MeshBitReader reader,
        int bitsPerComponent,
        int componentCount,
        Pdfe.Core.Primitives.PdfArray? decode)
    {
        var components = new double[componentCount];
        for (var i = 0; i < componentCount; i++)
        {
            var cMin = decode?.Count >= 6 + (2 * i) ? decode.GetNumber(4 + (2 * i)) : 0;
            var cMax = decode?.Count >= 6 + (2 * i) ? decode.GetNumber(5 + (2 * i)) : 1;
            components[i] = Decode(reader.Read(bitsPerComponent), bitsPerComponent, cMin, cMax);
        }

        return components;
    }

    private static double Decode(uint encoded, int bits, double min, double max)
    {
        var denominator = Math.Pow(2, bits) - 1;
        return min + encoded * ((max - min) / denominator);
    }

    private static int GetMeshComponentCount(
        Pdfe.Core.Primitives.PdfStream stream,
        PdfColorSpace colorSpace,
        Pdfe.Core.Primitives.PdfObject? function)
    {
        if (function != null)
            return 1;

        if (stream.GetOptional("Decode") is Pdfe.Core.Primitives.PdfArray decode && decode.Count > 4)
            return Math.Max(1, (decode.Count - 4) / 2);

        return Math.Max(1, colorSpace.Components);
    }

    private void DrawMeshPatches(IReadOnlyList<MeshPatch> patches, bool tensorPatch)
    {
        if (patches.Count == 0)
            return;

        var minX = patches.Min(p => p.MinX);
        var minY = patches.Min(p => p.MinY);
        var maxX = patches.Max(p => p.MaxX);
        var maxY = patches.Max(p => p.MaxY);
        if (maxX <= minX || maxY <= minY)
            return;

        var width = Math.Clamp((int)Math.Ceiling(maxX - minX) * 2, 16, 768);
        var height = Math.Clamp((int)Math.Ceiling(maxY - minY) * 2, 16, 768);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        foreach (var patch in patches)
        {
            if (tensorPatch && patch.Points.Count >= 16)
                RasterizeTensorPatch(bitmap, patch, minX, minY, maxX, maxY);
            else if (!tensorPatch && patch.Points.Count >= 12)
                RasterizeCoonsPatch(bitmap, patch, minX, minY, maxX, maxY);
            else
                RasterizeMeshPatch(bitmap, patch, minX, minY, maxX, maxY);
        }

        DrawMeshBitmap(bitmap, minX, minY, maxX, maxY);
    }

    private void DrawMeshTriangles(IReadOnlyList<MeshTriangle> triangles)
    {
        if (triangles.Count == 0)
            return;

        var minX = triangles.Min(t => t.MinX);
        var minY = triangles.Min(t => t.MinY);
        var maxX = triangles.Max(t => t.MaxX);
        var maxY = triangles.Max(t => t.MaxY);
        if (maxX <= minX || maxY <= minY)
            return;

        var width = Math.Clamp((int)Math.Ceiling(maxX - minX) * 2, 16, 1024);
        var height = Math.Clamp((int)Math.Ceiling(maxY - minY) * 2, 16, 1024);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        foreach (var triangle in triangles)
            RasterizeMeshTriangle(bitmap, triangle, minX, minY, maxX, maxY);

        DrawMeshBitmap(bitmap, minX, minY, maxX, maxY);
    }

    private void DrawMeshBitmap(SKBitmap bitmap, double minX, double minY, double maxX, double maxY)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var shader = SKShader.CreateImage(
            image,
            SKShaderTileMode.Clamp,
            SKShaderTileMode.Clamp,
            SKMatrix.CreateScale(
                (float)((maxX - minX) / bitmap.Width),
                (float)((maxY - minY) / bitmap.Height)));

        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = _state.BlendMode,
            Color = SKColors.White.WithAlpha((byte)Math.Clamp(_state.FillAlpha * 255, 0, 255)),
            IsAntialias = _options.AntiAlias
        };

        _canvas.Save();
        try
        {
            _canvas.Translate((float)minX, (float)minY);
            _canvas.DrawRect(
                new SKRect(0, 0, (float)(maxX - minX), (float)(maxY - minY)),
                paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private static void RasterizeMeshPatch(SKBitmap bitmap, MeshPatch patch, double minX, double minY, double maxX, double maxY)
    {
        var startX = Math.Clamp((int)Math.Floor((patch.MinX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((patch.MaxX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var startY = Math.Clamp((int)Math.Floor((patch.MinY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((patch.MaxY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / bitmap.Height) * (maxY - minY);
            var v = patch.MaxY > patch.MinY ? (py - patch.MinY) / (patch.MaxY - patch.MinY) : 0;
            v = Math.Clamp(v, 0, 1);

            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / bitmap.Width) * (maxX - minX);
                var u = patch.MaxX > patch.MinX ? (px - patch.MinX) / (patch.MaxX - patch.MinX) : 0;
                u = Math.Clamp(u, 0, 1);
                bitmap.SetPixel(x, bitmap.Height - 1 - y, Bilinear(patch.Colors, u, v));
            }
        }
    }

    private static void RasterizeTensorPatch(SKBitmap bitmap, MeshPatch patch, double minX, double minY, double maxX, double maxY)
    {
        const int steps = 24;
        var vertices = new MeshVertex[steps + 1, steps + 1];

        for (var row = 0; row <= steps; row++)
        {
            var v = row / (double)steps;
            for (var col = 0; col <= steps; col++)
            {
                var u = col / (double)steps;
                vertices[row, col] = new MeshVertex(
                    0,
                    EvaluateTensorPatchPoint(patch.Points, u, v),
                    Bilinear(patch.Colors, u, v));
            }
        }

        for (var row = 0; row < steps; row++)
        {
            for (var col = 0; col < steps; col++)
            {
                RasterizeMeshTriangle(
                    bitmap,
                    new MeshTriangle(vertices[row, col], vertices[row, col + 1], vertices[row + 1, col + 1]),
                    minX,
                    minY,
                    maxX,
                    maxY);
                RasterizeMeshTriangle(
                    bitmap,
                    new MeshTriangle(vertices[row, col], vertices[row + 1, col + 1], vertices[row + 1, col]),
                    minX,
                    minY,
                    maxX,
                    maxY);
            }
        }
    }

    private static void RasterizeCoonsPatch(SKBitmap bitmap, MeshPatch patch, double minX, double minY, double maxX, double maxY)
    {
        const int steps = 24;
        var vertices = new MeshVertex[steps + 1, steps + 1];

        for (var row = 0; row <= steps; row++)
        {
            var v = row / (double)steps;
            for (var col = 0; col <= steps; col++)
            {
                var u = col / (double)steps;
                vertices[row, col] = new MeshVertex(
                    0,
                    EvaluateTensorPatchPoint(patch.Points, u, v),
                    Bilinear(patch.Colors, u, v));
            }
        }

        for (var row = 0; row < steps; row++)
        {
            for (var col = 0; col < steps; col++)
            {
                RasterizeMeshTriangle(
                    bitmap,
                    new MeshTriangle(vertices[row, col], vertices[row, col + 1], vertices[row + 1, col + 1]),
                    minX,
                    minY,
                    maxX,
                    maxY);
                RasterizeMeshTriangle(
                    bitmap,
                    new MeshTriangle(vertices[row, col], vertices[row + 1, col + 1], vertices[row + 1, col]),
                    minX,
                    minY,
                    maxX,
                    maxY);
            }
        }
    }

    private static SKPoint EvaluateCoonsPatchPoint(IReadOnlyList<SKPoint> points, double u, double v)
    {
        var top = CubicBezier(points[0], points[1], points[2], points[3], u);
        var right = CubicBezier(points[3], points[4], points[5], points[6], v);
        var bottom = CubicBezier(points[9], points[8], points[7], points[6], u);
        var left = CubicBezier(points[0], points[11], points[10], points[9], v);

        var topLeft = points[0];
        var topRight = points[3];
        var bottomRight = points[6];
        var bottomLeft = points[9];

        var x =
            ((1 - v) * top.X) +
            (v * bottom.X) +
            ((1 - u) * left.X) +
            (u * right.X) -
            (((1 - u) * (1 - v) * topLeft.X) +
             (u * (1 - v) * topRight.X) +
             (u * v * bottomRight.X) +
             ((1 - u) * v * bottomLeft.X));
        var y =
            ((1 - v) * top.Y) +
            (v * bottom.Y) +
            ((1 - u) * left.Y) +
            (u * right.Y) -
            (((1 - u) * (1 - v) * topLeft.Y) +
             (u * (1 - v) * topRight.Y) +
             (u * v * bottomRight.Y) +
             ((1 - u) * v * bottomLeft.Y));

        return new SKPoint((float)x, (float)y);
    }

    private static SKPoint CubicBezier(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, double t)
    {
        var mt = 1 - t;
        var b0 = mt * mt * mt;
        var b1 = 3 * t * mt * mt;
        var b2 = 3 * t * t * mt;
        var b3 = t * t * t;
        return new SKPoint(
            (float)((p0.X * b0) + (p1.X * b1) + (p2.X * b2) + (p3.X * b3)),
            (float)((p0.Y * b0) + (p1.Y * b1) + (p2.Y * b2) + (p3.Y * b3)));
    }

    private static SKPoint EvaluateTensorPatchPoint(IReadOnlyList<SKPoint> points, double u, double v)
    {
        Span<double> bu = stackalloc double[4];
        Span<double> bv = stackalloc double[4];
        CubicBernstein(u, bu);
        CubicBernstein(v, bv);

        double x = 0;
        double y = 0;
        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 4; col++)
            {
                var point = GetTensorPatchPoint(points, row, col);
                var weight = bv[row] * bu[col];
                x += point.X * weight;
                y += point.Y * weight;
            }
        }

        return new SKPoint((float)x, (float)y);
    }

    private static void CubicBernstein(double t, Span<double> values)
    {
        var mt = 1 - t;
        values[0] = mt * mt * mt;
        values[1] = 3 * t * mt * mt;
        values[2] = 3 * t * t * mt;
        values[3] = t * t * t;
    }

    private static SKPoint GetTensorPatchPoint(IReadOnlyList<SKPoint> points, int row, int col)
        => points[(row * 4) + col];

    private static void RasterizeMeshTriangle(
        SKBitmap bitmap,
        MeshTriangle triangle,
        double minX,
        double minY,
        double maxX,
        double maxY)
    {
        var startX = Math.Clamp((int)Math.Floor((triangle.MinX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var endX = Math.Clamp((int)Math.Ceiling((triangle.MaxX - minX) / (maxX - minX) * bitmap.Width), 0, bitmap.Width - 1);
        var startY = Math.Clamp((int)Math.Floor((triangle.MinY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);
        var endY = Math.Clamp((int)Math.Ceiling((triangle.MaxY - minY) / (maxY - minY) * bitmap.Height), 0, bitmap.Height - 1);

        var a = triangle.A.Point;
        var b = triangle.B.Point;
        var c = triangle.C.Point;
        var denominator =
            (b.Y - c.Y) * (a.X - c.X) +
            (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(denominator) < 1e-9)
            return;

        for (var y = startY; y <= endY; y++)
        {
            var py = minY + ((y + 0.5) / bitmap.Height) * (maxY - minY);
            for (var x = startX; x <= endX; x++)
            {
                var px = minX + ((x + 0.5) / bitmap.Width) * (maxX - minX);
                var wa = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / denominator;
                var wb = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / denominator;
                var wc = 1 - wa - wb;
                const double epsilon = -0.001;
                if (wa < epsilon || wb < epsilon || wc < epsilon)
                    continue;

                bitmap.SetPixel(x, y, Barycentric(
                    triangle.A.Color,
                    triangle.B.Color,
                    triangle.C.Color,
                    wa,
                    wb,
                    wc));
            }
        }
    }

    private static SKColor Barycentric(SKColor a, SKColor b, SKColor c, double wa, double wb, double wc)
    {
        return new SKColor(
            (byte)Math.Clamp((a.Red * wa) + (b.Red * wb) + (c.Red * wc), 0, 255),
            (byte)Math.Clamp((a.Green * wa) + (b.Green * wb) + (c.Green * wc), 0, 255),
            (byte)Math.Clamp((a.Blue * wa) + (b.Blue * wb) + (c.Blue * wc), 0, 255),
            255);
    }

    private static SKColor Bilinear(SKColor[] colors, double u, double v)
    {
        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        var r0 = Lerp(colors[0].Red, colors[1].Red, u);
        var r1 = Lerp(colors[3].Red, colors[2].Red, u);
        var g0 = Lerp(colors[0].Green, colors[1].Green, u);
        var g1 = Lerp(colors[3].Green, colors[2].Green, u);
        var b0 = Lerp(colors[0].Blue, colors[1].Blue, u);
        var b1 = Lerp(colors[3].Blue, colors[2].Blue, u);
        return new SKColor(
            (byte)Math.Clamp(Lerp(r0, r1, v), 0, 255),
            (byte)Math.Clamp(Lerp(g0, g1, v), 0, 255),
            (byte)Math.Clamp(Lerp(b0, b1, v), 0, 255),
            255);
    }

    private static SKMatrix? InvertAffine(SKMatrix matrix)
    {
        var det = matrix.ScaleX * matrix.ScaleY - matrix.SkewX * matrix.SkewY;
        if (Math.Abs(det) < 1e-9)
            return null;

        var invA = matrix.ScaleY / det;
        var invB = -matrix.SkewY / det;
        var invC = -matrix.SkewX / det;
        var invD = matrix.ScaleX / det;
        var invE = -(invA * matrix.TransX + invC * matrix.TransY);
        var invF = -(invB * matrix.TransX + invD * matrix.TransY);
        return new SKMatrix(invA, invC, invE, invB, invD, invF, 0, 0, 1);
    }

    private void RenderShading(string nameOperand)
    {
        // Remove leading / if present
        var name = nameOperand.TrimStart('/');

        var shading = ResolveShadingFromActiveResources(name);
        if (shading == null)
            return;

        var shadingType = shading.GetInt("ShadingType", 0);

        // Handle different shading types
        switch (shadingType)
        {
            case 1: // Function-based shading
                RenderFunctionShading(shading);
                break;
            case 2: // Axial shading (linear gradient)
                RenderAxialShading(shading);
                break;
            case 3: // Radial shading (radial gradient)
                RenderRadialShading(shading);
                break;
            case 4: // Free-form Gouraud triangle mesh
            case 5: // Lattice-form Gouraud triangle mesh
            case 6: // Coons patch mesh
            case 7: // Tensor-product patch mesh
                RenderMeshShading(shading, shadingType);
                break;
            default:
                // Shading fills the current clipping path
                break;
        }
    }

    private void RenderMeshShading(Pdfe.Core.Primitives.PdfDictionary shading, int shadingType)
    {
        if (shading is not Pdfe.Core.Primitives.PdfStream stream)
            return;

        _canvas.Save();
        try
        {
            ApplyShadingBoundingBoxClip(shading);
            switch (shadingType)
            {
                case 4:
                    DrawMeshTriangles(DecodeType4MeshTriangles(stream));
                    break;
                case 5:
                    DrawMeshTriangles(DecodeType5MeshTriangles(stream));
                    break;
                case 6:
                    DrawMeshPatches(DecodeType6MeshPatches(stream, tensorPatch: false), tensorPatch: false);
                    break;
                case 7:
                    DrawMeshPatches(DecodeType6MeshPatches(stream, tensorPatch: true), tensorPatch: true);
                    break;
            }
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private void RenderAxialShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        // Get the coordinate array [x0, y0, x1, y1]
        var coords = shading.GetOptional("Coords") as Pdfe.Core.Primitives.PdfArray;
        if (coords == null || coords.Count < 4)
            return;

        var x0 = (float)coords.GetNumber(0);
        var y0 = (float)coords.GetNumber(1);
        var x1 = (float)coords.GetNumber(2);
        var y1 = (float)coords.GetNumber(3);

        var (startColor, endColor, stops, positions) = ResolveGradientColors(shading);

        // Create the gradient shader
        using var shader = stops != null && stops.Length > 2
            ? SKShader.CreateLinearGradient(
                new SKPoint(x0, y0),
                new SKPoint(x1, y1),
                stops,
                positions,
                SKShaderTileMode.Clamp)
            : SKShader.CreateLinearGradient(
                new SKPoint(x0, y0),
                new SKPoint(x1, y1),
                new[] { startColor, endColor },
                null,
                SKShaderTileMode.Clamp);

        _canvas.Save();
        try
        {
            ApplyShadingBoundingBoxClip(shading);
            DrawShaderOverCurrentClip(shader);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private void RenderRadialShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        // Get the coordinate array [x0, y0, r0, x1, y1, r1]
        var coords = shading.GetOptional("Coords") as Pdfe.Core.Primitives.PdfArray;
        if (coords == null || coords.Count < 6)
            return;

        var x0 = (float)coords.GetNumber(0);
        var y0 = (float)coords.GetNumber(1);
        var r0 = (float)coords.GetNumber(2);
        var x1 = (float)coords.GetNumber(3);
        var y1 = (float)coords.GetNumber(4);
        var r1 = (float)coords.GetNumber(5);

        var (startColor, endColor, stops, positions) = ResolveGradientColors(shading);

        // Create the two-point conical gradient
        using var shader = stops != null && stops.Length > 2
            ? SKShader.CreateTwoPointConicalGradient(
                new SKPoint(x0, y0), r0,
                new SKPoint(x1, y1), r1,
                stops,
                positions,
                SKShaderTileMode.Clamp)
            : SKShader.CreateTwoPointConicalGradient(
                new SKPoint(x0, y0), r0,
                new SKPoint(x1, y1), r1,
                new[] { startColor, endColor },
                null,
                SKShaderTileMode.Clamp);

        var (extendStart, extendEnd) = GetShadingExtend(shading);
        _canvas.Save();
        try
        {
            ApplyShadingBoundingBoxClip(shading);
            ApplyRadialShadingDomainClip(x0, y0, r0, x1, y1, r1, extendStart, extendEnd);
            DrawShaderOverCurrentClip(shader);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private void ApplyRadialShadingDomainClip(
        float x0,
        float y0,
        float r0,
        float x1,
        float y1,
        float r1,
        bool extendStart,
        bool extendEnd)
    {
        if (!extendEnd)
        {
            using var endPath = new SKPath();
            endPath.AddCircle(x1, y1, Math.Max(0, r1));
            _canvas.ClipPath(endPath, SKClipOperation.Intersect, _options.AntiAlias);
        }

        if (!extendStart && r0 > 0)
        {
            using var startPath = new SKPath();
            startPath.AddCircle(x0, y0, r0);
            _canvas.ClipPath(startPath, SKClipOperation.Difference, _options.AntiAlias);
        }
    }

    private static (bool Start, bool End) GetShadingExtend(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        if (shading.GetOptional("Extend") is not Pdfe.Core.Primitives.PdfArray extend)
            return (false, false);

        return (
            extend.Count > 0 && extend[0] is Pdfe.Core.Primitives.PdfBoolean start && start.Value,
            extend.Count > 1 && extend[1] is Pdfe.Core.Primitives.PdfBoolean end && end.Value);
    }

    private void ApplyShadingBoundingBoxClip(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var bbox = GetShadingBoundingBox(shading);
        if (bbox.HasValue)
            _canvas.ClipRect(bbox.Value, SKClipOperation.Intersect, _options.AntiAlias);
    }

    private SKRect? GetShadingBoundingBox(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var bbox = GetNumberArray(shading.GetOptional("BBox") as Pdfe.Core.Primitives.PdfArray);
        if (bbox is not { Length: >= 4 })
            return null;

        var rect = new SKRect(
            (float)Math.Min(bbox[0], bbox[2]),
            (float)Math.Min(bbox[1], bbox[3]),
            (float)Math.Max(bbox[0], bbox[2]),
            (float)Math.Max(bbox[1], bbox[3]));
        return rect.Width > 0 && rect.Height > 0
            ? rect
            : null;
    }

    private void DrawShaderOverCurrentClip(SKShader shader)
    {
        var clipBounds = _canvas.LocalClipBounds;
        if (clipBounds.Width <= 0 || clipBounds.Height <= 0)
            return;

        var alpha = (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255);
        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = alpha == 255 ? _state.BlendMode : SKBlendMode.SrcOver,
            IsAntialias = _options.AntiAlias
        };

        if (alpha == 255)
        {
            _canvas.DrawRect(clipBounds, paint);
            return;
        }

        using var layerPaint = new SKPaint
        {
            Color = SKColors.White.WithAlpha(alpha),
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };
        _canvas.SaveLayer(clipBounds, layerPaint);
        try
        {
            _canvas.DrawRect(clipBounds, paint);
        }
        finally
        {
            _canvas.Restore();
        }
    }

    private void RenderFunctionShading(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var funcRef = shading.GetOptional("Function");
        var funcObj = funcRef != null ? _page.Document.Resolve(funcRef) : null;
        var colorSpace = ResolveShadingColorSpace(shading);
        var domain = GetNumberArray(shading.GetOptional("Domain") as Pdfe.Core.Primitives.PdfArray)
                     ?? new[] { 0.0, 1.0, 0.0, 1.0 };
        if (domain.Length < 4)
            return;

        var matrix = GetMatrix(shading.GetOptional("Matrix") as Pdfe.Core.Primitives.PdfArray);
        var inverseMatrix = InvertAffine(matrix);
        if (!inverseMatrix.HasValue)
            return;

        var bounds = _canvas.LocalClipBounds;
        var bbox = GetNumberArray(shading.GetOptional("BBox") as Pdfe.Core.Primitives.PdfArray);
        if (bbox is { Length: >= 4 })
        {
            var bboxRect = new SKRect(
                (float)Math.Min(bbox[0], bbox[2]),
                (float)Math.Min(bbox[1], bbox[3]),
                (float)Math.Max(bbox[0], bbox[2]),
                (float)Math.Max(bbox[1], bbox[3]));
            bounds.Intersect(bboxRect);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var width = Math.Clamp((int)Math.Ceiling(bounds.Width), 1, 1024);
        var height = Math.Clamp((int)Math.Ceiling(bounds.Height), 1, 1024);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        var inv = inverseMatrix.Value;
        var xMin = Math.Min(domain[0], domain[1]);
        var xMax = Math.Max(domain[0], domain[1]);
        var yMin = Math.Min(domain[2], domain[3]);
        var yMax = Math.Max(domain[2], domain[3]);
        var alpha = (byte)Math.Clamp(_state.FillAlpha * 255, 0, 255);

        for (var y = 0; y < height; y++)
        {
            var targetY = bounds.Top + ((y + 0.5f) / height) * bounds.Height;
            for (var x = 0; x < width; x++)
            {
                var targetX = bounds.Left + ((x + 0.5f) / width) * bounds.Width;
                var sourceX = inv.ScaleX * targetX + inv.SkewX * targetY + inv.TransX;
                var sourceY = inv.SkewY * targetX + inv.ScaleY * targetY + inv.TransY;
                if (sourceX < xMin || sourceX > xMax || sourceY < yMin || sourceY > yMax)
                    continue;

                var comps = PdfFunctionEvaluator.Evaluate(funcObj, new[] { (double)sourceX, (double)sourceY }, _page.Document);
                if (comps == null)
                    continue;

                var color = ComponentsToSkColor(comps, colorSpace);
                bitmap.SetPixel(x, y, color.WithAlpha(alpha));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint
        {
            BlendMode = _state.BlendMode,
            IsAntialias = _options.AntiAlias
        };
        _canvas.DrawImage(image, bounds, paint);
    }

    private double[]? GetNumberArray(Pdfe.Core.Primitives.PdfArray? arr)
    {
        if (arr == null)
            return null;

        var values = new double[arr.Count];
        for (var i = 0; i < arr.Count; i++)
            values[i] = ArrayNumberOrDefault(arr, i);
        return values;
    }

    private (SKColor start, SKColor end, SKColor[]? stops, float[]? positions) ResolveGradientColors(
        Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var colorSpace = ResolveShadingColorSpace(shading);
        var funcRef = shading.GetOptional("Function");
        var funcObj = funcRef != null ? _page.Document.Resolve(funcRef) : null;
        var domain = GetNumberArray(shading.GetOptional("Domain") as Pdfe.Core.Primitives.PdfArray)
                     ?? GetNumberArray((funcObj as Pdfe.Core.Primitives.PdfDictionary)?.GetOptional("Domain") as Pdfe.Core.Primitives.PdfArray)
                     ?? new[] { 0.0, 1.0 };
        var domainMin = domain.Length >= 2 ? domain[0] : 0.0;
        var domainMax = domain.Length >= 2 ? domain[1] : 1.0;
        if (Math.Abs(domainMax - domainMin) < 1e-9)
            domainMax = domainMin + 1.0;

        var c0 = PdfFunctionEvaluator.Evaluate(funcObj, domainMin, _page.Document) ?? new[] { 0.0 };
        var c1 = PdfFunctionEvaluator.Evaluate(funcObj, domainMax, _page.Document) ?? new[] { 1.0 };

        var startColor = ComponentsToSkColor(c0, colorSpace);
        var endColor = ComponentsToSkColor(c1, colorSpace);

        var (stops, positions) = ShouldSampleGradientFunction(funcObj)
            ? SampleGradientFunction(funcObj, colorSpace, domainMin, domainMax)
            : (null, null);

        return (startColor, endColor, stops, positions);
    }

    private (SKColor[] stops, float[] positions) SampleGradientFunction(
        PdfObject? funcObj,
        PdfColorSpace colorSpace,
        double domainMin,
        double domainMax)
    {
        var stops = new SKColor[ComplexGradientSampleCount + 1];
        var positions = new float[ComplexGradientSampleCount + 1];

        for (var i = 0; i <= ComplexGradientSampleCount; i++)
        {
            var position = (double)i / ComplexGradientSampleCount;
            var t = domainMin + ((domainMax - domainMin) * position);
            var comps = PdfFunctionEvaluator.Evaluate(funcObj, t, _page.Document) ?? new[] { 0.0 };
            stops[i] = ComponentsToSkColor(comps, colorSpace);
            positions[i] = (float)position;
        }

        return (stops, positions);
    }

    private PdfColorSpace ResolveShadingColorSpace(Pdfe.Core.Primitives.PdfDictionary shading)
    {
        var colorSpaceObj = shading.GetOptional("ColorSpace");
        if (colorSpaceObj == null)
            return PdfColorSpace.DeviceGray;

        try
        {
            if (colorSpaceObj is PdfName name)
                return ResolveColorSpace(name.Value) ?? PdfColorSpace.Parse(colorSpaceObj, _page.Document);

            return PdfColorSpace.Parse(colorSpaceObj, _page.Document);
        }
        catch
        {
            return PdfColorSpace.DeviceGray;
        }
    }

    private bool ShouldSampleGradientFunction(PdfObject? funcObj)
    {
        if (funcObj == null)
            return false;

        var resolved = _page.Document.Resolve(funcObj);
        if (resolved is PdfArray array)
            return array.Any(ShouldSampleGradientFunction);

        if (resolved is not PdfDictionary function)
            return false;

        return function.GetInt("FunctionType", -1) switch
        {
            0 or 3 or 4 => true,
            2 => Math.Abs(function.GetNumber("N", 1.0) - 1.0) > 1e-9,
            _ => false
        };
    }

    private static SKColor ComponentsToSkColor(double[] comps, string colorSpace)
    {
        return colorSpace switch
        {
            "DeviceGray" or "G" =>
                comps.Length >= 1 ? ToGray(comps[0]) : SKColors.Black,
            "DeviceRGB" or "RGB" =>
                comps.Length >= 3 ? ToRGB(comps[0], comps[1], comps[2]) : SKColors.Black,
            "DeviceCMYK" or "CMYK" =>
                comps.Length >= 4 ? CmykToColor(comps[0], comps[1], comps[2], comps[3]) : SKColors.Black,
            _ => comps.Length >= 3 ? ToRGB(comps[0], comps[1], comps[2])
               : comps.Length >= 1 ? ToGray(comps[0]) : SKColors.Black
        };
    }

    private static SKColor ComponentsToSkColor(double[] comps, PdfColorSpace colorSpace)
    {
        if (comps.Length == 0)
            return SKColors.Black;

        var (r, g, b) = colorSpace.ToRgb(comps);
        return ToRGB(r, g, b);
    }

    private static SKColor ToGray(double g)
    {
        byte v = (byte)Math.Clamp(g * 255, 0, 255);
        return new SKColor(v, v, v);
    }

    private static SKColor ToRGB(double r, double g, double b) =>
        new SKColor(
            (byte)Math.Clamp(r * 255, 0, 255),
            (byte)Math.Clamp(g * 255, 0, 255),
            (byte)Math.Clamp(b * 255, 0, 255));

    #endregion

    #region Color Space Operators (SC, SCN, sc, scn)

    private void SetStrokingColor(IReadOnlyList<PdfObject> operands)
    {
        var color = ParseColorFromOperands(operands, _state.StrokeColorSpace);
        if (color.HasValue)
        {
            _state.StrokeColor = color.Value;
            _state.StrokeDeviceCmyk = TryParseDeviceCmykOperands(operands, _state.StrokeColorSpace);
        }
    }

    private void SetNonStrokingColor(IReadOnlyList<PdfObject> operands)
    {
        var fillColorSpace = ResolveColorSpace(_state.FillColorSpace);
        if (fillColorSpace?.Type == PdfColorSpaceType.Pattern)
        {
            _state.FillPatternName = operands.OfType<PdfName>().FirstOrDefault()?.Value;
            _state.FillDeviceCmyk = null;
            var tintColor = ParsePatternTintColor(operands, _state.FillColorSpace);
            if (tintColor.HasValue)
                _state.FillColor = tintColor.Value;
            return;
        }

        var color = ParseColorFromOperands(operands, _state.FillColorSpace);
        if (color.HasValue)
        {
            _state.FillColor = color.Value;
            _state.FillDeviceCmyk = TryParseDeviceCmykOperands(operands, _state.FillColorSpace);
            _state.FillPatternName = null;
        }
    }

    private SKColor? ParseColorFromOperands(IReadOnlyList<PdfObject> operands, string colorSpace)
    {
        var values = operands
            .Where(o => o is not PdfName)
            .Select(o => o.GetNumber())
            .ToArray();

        if (values.Length == 0)
            return null;

        var cs = ResolveColorSpace(colorSpace);
        if (cs != null && cs.Type != PdfColorSpaceType.Pattern)
        {
            var (r, g, b) = cs.ToRgb(values);
            return RgbToColor(r, g, b);
        }

        return colorSpace switch
        {
            "Pattern" when operands.Any(o => o is PdfName) =>
                null,

            _ => null
        };
    }

    private DeviceCmykColor? TryParseDeviceCmykOperands(IReadOnlyList<PdfObject> operands, string colorSpace)
    {
        var values = operands
            .Where(o => o is not PdfName)
            .Select(o => o.GetNumber())
            .ToArray();

        var cs = ResolveColorSpace(colorSpace);
        if (cs?.Type == PdfColorSpaceType.DeviceCMYK)
        {
            return values.Length >= 4
            ? new DeviceCmykColor(values[0], values[1], values[2], values[3])
            : null;
        }

        return TryEvaluateTintTransformToDeviceCmyk(colorSpace, values);
    }

    private DeviceCmykColor? TryEvaluateTintTransformToDeviceCmyk(string colorSpace, double[] values)
    {
        if (values.Length == 0)
            return null;

        var colorSpaceObj = ResolveColorSpaceObject(colorSpace);
        if (colorSpaceObj == null)
            return null;

        var resolved = _page.Document.Resolve(colorSpaceObj);
        if (resolved is not PdfArray arr || arr.Count < 4 || arr[0] is not PdfName typeName)
            return null;

        if (typeName.Value is not ("Separation" or "DeviceN"))
            return null;

        var alternateSpace = PdfColorSpace.Parse(arr[2], _page.Document);
        if (alternateSpace.Type != PdfColorSpaceType.DeviceCMYK)
            return null;

        var evaluated = PdfFunctionEvaluator.Evaluate(arr[3], values, _page.Document);
        return evaluated is { Length: >= 4 }
            ? new DeviceCmykColor(evaluated[0], evaluated[1], evaluated[2], evaluated[3])
            : null;
    }

    private SKColor? ParsePatternTintColor(IReadOnlyList<PdfObject> operands, string colorSpaceName)
    {
        var colorSpaceObj = ResolveColorSpaceObject(colorSpaceName);
        var resolved = colorSpaceObj != null ? _page.Document.Resolve(colorSpaceObj) : null;
        if (resolved is not PdfArray arr ||
            arr.Count < 2 ||
            arr[0] is not PdfName typeName ||
            typeName.Value != "Pattern")
        {
            return null;
        }

        var values = operands
            .Where(o => o is not PdfName)
            .Select(o => o.GetNumber())
            .ToArray();
        if (values.Length == 0)
            return null;

        var baseColorSpace = ResolvePatternBaseColorSpace(arr[1]);
        if (baseColorSpace == null || baseColorSpace.Type == PdfColorSpaceType.Pattern)
            return null;

        var (r, g, b) = baseColorSpace.ToRgb(values);
        return RgbToColor(r, g, b);
    }

    private PdfColorSpace? ResolvePatternBaseColorSpace(PdfObject colorSpaceObj)
    {
        if (colorSpaceObj is PdfName name)
        {
            var named = ResolveColorSpace(name.Value);
            if (named != null)
                return named;

            var resourceObj = ResolveColorSpaceObject(name.Value);
            return resourceObj != null
                ? PdfColorSpace.Parse(resourceObj, _page.Document)
                : null;
        }

        return PdfColorSpace.Parse(colorSpaceObj, _page.Document);
    }

    private PdfColorSpace? ResolveColorSpace(string name)
    {
        var defaultCsObj = ResolveDefaultColorSpaceObject(name);
        if (defaultCsObj != null)
            return PdfColorSpace.Parse(defaultCsObj, _page.Document);

        var cs = PdfColorSpace.FromName(name, _page.Document);
        if (cs.Type != PdfColorSpaceType.Unknown)
            return cs;

        var csObj = ResolveColorSpaceObject(name);
        return csObj != null ? PdfColorSpace.Parse(csObj, _page.Document) : null;
    }

    private PdfObject? ResolveDefaultColorSpaceObject(string deviceColorSpaceName)
    {
        var defaultName = deviceColorSpaceName switch
        {
            "DeviceGray" or "G" => "DefaultGray",
            "DeviceRGB" or "RGB" => "DefaultRGB",
            "DeviceCMYK" or "CMYK" => "DefaultCMYK",
            _ => null
        };

        return defaultName != null ? ResolveColorSpaceObject(defaultName) : null;
    }

    private PdfObject? ResolveColorSpaceObject(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var colorSpacesObj = resources.GetOptional("ColorSpace");
            if (colorSpacesObj == null) continue;
            if (_page.Document.Resolve(colorSpacesObj) is not Pdfe.Core.Primitives.PdfDictionary colorSpaces)
                continue;

            var csObj = colorSpaces.GetOptional(name);
            if (csObj != null)
                return csObj;
        }

        return _page.GetColorSpaceObject(name);
    }

    /// <summary>
    /// Walk the resources stack top-down looking for a font definition.
    /// The innermost Form XObject's /Resources wins; we fall through to
    /// outer XObjects and finally the page when the name isn't defined
    /// locally — matches the "inherit if not found" rule from PDF 32000-2
    /// §7.8.3 for Form XObject resource resolution.
    /// </summary>
    private Pdfe.Core.Primitives.PdfDictionary? ResolveFontFromActiveResources(string fontName)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var fontsObj = resources.GetOptional("Font");
            if (fontsObj == null) continue;
            if (_page.Document.Resolve(fontsObj) is not Pdfe.Core.Primitives.PdfDictionary fonts)
                continue;
            var fontObj = fonts.GetOptional(fontName);
            if (fontObj == null) continue;
            return _page.Document.Resolve(fontObj) as Pdfe.Core.Primitives.PdfDictionary;
        }
        return null;
    }

    /// <summary>
    /// Stack-aware XObject lookup, same fallback rule as
    /// <see cref="ResolveFontFromActiveResources"/>. Returns the resolved
    /// XObject (typically a stream); caller checks /Subtype.
    /// </summary>
    private Pdfe.Core.Primitives.PdfObject? ResolveXObjectFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var xobjsObj = resources.GetOptional("XObject");
            if (xobjsObj == null) continue;
            if (_page.Document.Resolve(xobjsObj) is not Pdfe.Core.Primitives.PdfDictionary xobjs)
                continue;
            var x = xobjs.GetOptional(name);
            if (x == null) continue;
            return _page.Document.Resolve(x);
        }
        return null;
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolveExtGStateFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var extGStatesObj = resources.GetOptional("ExtGState");
            if (extGStatesObj == null) continue;
            if (_page.Document.Resolve(extGStatesObj) is not Pdfe.Core.Primitives.PdfDictionary extGStates)
                continue;
            var extGState = extGStates.GetOptional(name);
            if (extGState == null) continue;
            return _page.Document.Resolve(extGState) as Pdfe.Core.Primitives.PdfDictionary;
        }

        return null;
    }

    private Pdfe.Core.Primitives.PdfObject? ResolvePropertyFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var propertiesObj = resources.GetOptional("Properties");
            if (propertiesObj == null) continue;
            if (_page.Document.Resolve(propertiesObj) is not Pdfe.Core.Primitives.PdfDictionary properties)
                continue;
            var property = properties.GetOptional(name);
            if (property != null)
                return property;
        }

        return null;
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolvePatternFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var patternsObj = resources.GetOptional("Pattern");
            if (patternsObj == null) continue;
            if (_page.Document.Resolve(patternsObj) is not Pdfe.Core.Primitives.PdfDictionary patterns)
                continue;
            var pattern = patterns.GetOptional(name);
            if (pattern == null) continue;
            return _page.Document.Resolve(pattern) as Pdfe.Core.Primitives.PdfDictionary;
        }

        return null;
    }

    private Pdfe.Core.Primitives.PdfDictionary? ResolveShadingFromActiveResources(string name)
    {
        foreach (var resources in _resourcesStack)
        {
            if (resources == null) continue;
            var shadingsObj = resources.GetOptional("Shading");
            if (shadingsObj == null) continue;
            if (_page.Document.Resolve(shadingsObj) is not Pdfe.Core.Primitives.PdfDictionary shadings)
                continue;
            var shadingObj = shadings.GetOptional(name);
            if (shadingObj == null) continue;
            return _page.Document.Resolve(shadingObj) as Pdfe.Core.Primitives.PdfDictionary;
        }

        return _page.GetShading(name);
    }

    #endregion
}
