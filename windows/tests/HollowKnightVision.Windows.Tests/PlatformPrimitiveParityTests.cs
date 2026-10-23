using HollowKnightVision.Windows.Core;

internal static class PlatformPrimitiveParityTests
{
    private const int FrameWidth = 384;
    private const int FrameHeight = 216;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void Near(double expected, double actual, double tolerance, string label) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {label} {expected:F3}, received {actual:F3}.");

    public static void testTensorAccessorReadsContiguousFloat32Output()
    {
        var scores = Enumerable.Range(0, 8)
            .Select(index => index + .25f)
            .ToArray();
        var boxes = new float[]
        {
            // Four contiguous [1,4,2,2] planes: x, y, width, height.
            .05f, .25f, .45f, .65f,
            .10f, .20f, .30f, .40f,
            .10f, .10f, .10f, .10f,
            .12f, .12f, .12f, .12f
        };
        string[] classes = ["class-0", "class-1"];

        var decoded = DetectionDecoder.DecodeRaw(
            scores, [1, 2, 2, 2], boxes, [1, 4, 2, 2], classes,
            confidenceFloor: 0, maximumDetections: 8);

        Check(decoded.Count == 8,
            $"Expected all eight contiguous tensor values, received {decoded.Count}.");
        for (var index = 0; index < scores.Length; index++)
        {
            var classIndex = index / 4;
            var cell = index % 4;
            var detection = decoded.Single(candidate =>
                candidate.ClassIdentifier == classes[classIndex]
                && candidate.Confidence == index + .25f);
            Near(boxes[cell], detection.Box.X, 0, $"box {index} x");
            Near(boxes[4 + cell], detection.Box.Y, 0, $"box {index} y");
            Near(boxes[8 + cell], detection.Box.Width, 0, $"box {index} width");
            Near(boxes[12 + cell], detection.Box.Height, 0, $"box {index} height");
        }
        Check(decoded.Select(candidate => (candidate.ClassIdentifier, candidate.Box))
                .Distinct().Count() == 8,
            "Channel-major values did not retain eight unique class/box candidates.");
    }

    public static void testPersistentVisionRegistrationReportsAdjacentFrameTransforms()
    {
        var offsets = new (int X, int BottomOriginY)[]
        {
            (0, 0), (-8, 0), (-16, -4), (-12, -4), (0, 0)
        };
        var expected = new (double X, double Y)[]
        {
            (8, 0), (8, 4), (-4, 0), (-12, -4)
        };

        foreach (var useLiveRoi in new[] { false, true })
        {
            var tracker = new MaskedFrameMotionTracker();
            var bridge = new TransitionMotionBridge();
            bridge.Anchor(new AtlasPoint(0, 0));
            var exclusions = useLiveRoi ? LiveRoiExclusions() : [];
            var transforms = new List<CameraVector>();

            for (var index = 0; index < offsets.Length; index++)
            {
                var offset = offsets[index];
                var frame = AsymmetricTexture(
                    offset.X, offset.BottomOriginY, captureId: index);
                var diagnostic = tracker.Observe(frame, exclusions);
                if (index == 0)
                {
                    Check(diagnostic is null,
                        "The first persistent registration frame was not a warmup.");
                    continue;
                }

                var result = bridge.Track(
                    diagnostic, FrameWidth, FrameHeight, FrameWidth);
                Check(result is not null,
                    $"Persistent registration produced no transform at frame {index} (ROI={useLiveRoi}); diagnostic={diagnostic}.");
                transforms.Add(result!.CameraStep);
            }

            Check(transforms.Count == expected.Length,
                $"Expected {expected.Length} adjacent transforms, received {transforms.Count} (ROI={useLiveRoi}).");
            for (var index = 0; index < expected.Length; index++)
            {
                Near(expected[index].X, transforms[index].X, 1.5,
                    $"transform {index} x (ROI={useLiveRoi})");
                Near(expected[index].Y, transforms[index].Y, 1.5,
                    $"transform {index} y (ROI={useLiveRoi})");
            }
        }
    }

    public static void testVisionRegistrationMovesShiftedFrameBackToReference()
    {
        var step = RegisterMacCameraSolverPattern(
            horizontalShift: -8,
            topLeftVerticalShift: 0);

        Near(8, step.X, 1.5, "horizontal registration x");
        Near(0, step.Y, 1.5, "horizontal registration y");
    }

    public static void testVisionRegistrationReportsPositiveYForDownwardTopLeftPixelShift()
    {
        var step = RegisterMacCameraSolverPattern(
            horizontalShift: 0,
            topLeftVerticalShift: 8);

        Near(0, step.X, 1.5, "downward registration x");
        Near(8, step.Y, 1.5, "downward registration y");
    }

    private static CameraVector RegisterMacCameraSolverPattern(
        int horizontalShift,
        int topLeftVerticalShift)
    {
        const int width = 256;
        const int height = 128;
        var tracker = new MaskedFrameMotionTracker();
        var bridge = new TransitionMotionBridge();
        bridge.Anchor(new AtlasPoint(0, 0));

        var warmup = tracker.Observe(
            MacCameraSolverPattern(0, 0, captureId: 0), []);
        Check(warmup is null,
            "The reference registration frame was not treated as a warmup.");
        var diagnostic = tracker.Observe(
            MacCameraSolverPattern(
                horizontalShift,
                topLeftVerticalShift,
                captureId: 1),
            []);
        Check(diagnostic?.Motion is not null,
            $"The shifted Mac fixture did not register: {diagnostic}.");

        var result = bridge.Track(diagnostic, width, height, solveWidth: width);
        Check(result is not null,
            $"The production transition bridge rejected the Mac fixture: {diagnostic}.");
        return result!.CameraStep;
    }

    private static BgraFrame MacCameraSolverPattern(
        int horizontalShift,
        int topLeftVerticalShift,
        long captureId)
    {
        const int width = 256;
        const int height = 128;
        var pixels = new byte[width * height * BgraFrame.BytesPerPixel];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - horizontalShift;
                var sourceY = y - topLeftVerticalShift;
                var offset = (y * width + x) * BgraFrame.BytesPerPixel;
                pixels[offset + 3] = 255;
                var firstShape = sourceX is >= 28 and < 83
                    && sourceY is >= 19 and < 54;
                var secondShape = sourceX is >= 126 and < 211
                    && sourceY is >= 67 and < 108;
                var narrowEdge = sourceX is >= 96 and < 104
                    && sourceY is >= 35 and < 119;
                if (firstShape)
                {
                    pixels[offset] = 44;
                    pixels[offset + 1] = 192;
                    pixels[offset + 2] = 238;
                }
                else if (secondShape)
                {
                    pixels[offset] = 219;
                    pixels[offset + 1] = 91;
                    pixels[offset + 2] = 63;
                }
                else if (narrowEdge)
                {
                    pixels[offset] = 242;
                    pixels[offset + 1] = 231;
                    pixels[offset + 2] = 116;
                }
                else
                {
                    var texture = (byte)((sourceX * 7 + sourceY * 13
                        + sourceX * sourceY) & 31);
                    pixels[offset] = (byte)(8 + texture);
                    pixels[offset + 1] = (byte)(12 + texture);
                    pixels[offset + 2] = (byte)(18 + texture);
                }
            }
        }
        return new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            pixels,
            captureId,
            DateTimeOffset.UnixEpoch.AddMilliseconds(captureId));
    }

    private static IReadOnlyList<StencilRect> LiveRoiExclusions()
    {
        const double normalizedX = .06;
        const double normalizedBottomY = .07;
        const double normalizedWidth = .88;
        const double normalizedHeight = .70;
        var left = normalizedX * FrameWidth;
        var top = (1 - normalizedBottomY - normalizedHeight) * FrameHeight;
        var right = left + normalizedWidth * FrameWidth;
        var bottom = top + normalizedHeight * FrameHeight;
        return
        [
            new(0, 0, FrameWidth, top),
            new(0, bottom, FrameWidth, FrameHeight - bottom),
            new(0, top, left, bottom - top),
            new(right, top, FrameWidth - right, bottom - top)
        ];
    }

    private static BgraFrame AsymmetricTexture(
        int offsetX,
        int bottomOriginOffsetY,
        long captureId)
    {
        var pixels = new byte[FrameWidth * FrameHeight * BgraFrame.BytesPerPixel];
        for (var pixel = 0; pixel < pixels.Length; pixel += BgraFrame.BytesPerPixel)
        {
            pixels[pixel] = 15;
            pixels[pixel + 1] = 10;
            pixels[pixel + 2] = 8;
            pixels[pixel + 3] = 255;
        }

        ulong state = 0xD1CE_BA5E_5EED;
        ulong Next()
        {
            state = unchecked(state * 6_364_136_223_846_793_005
                + 1_442_695_040_888_963_407);
            return state;
        }

        for (var index = 0; index < 180; index++)
        {
            var x = (int)(Next() % 350) + 12 + offsetX;
            var bottomY = (int)(Next() % 182) + 12 + bottomOriginOffsetY;
            var width = 2 + (int)(Next() % 13);
            var height = 2 + (int)(Next() % 11);
            var red = (byte)((Next() >> 8) % 220 + 25);
            var green = (byte)((Next() >> 16) % 220 + 25);
            var blue = (byte)((Next() >> 24) % 220 + 25);
            var alpha = index % 5 == 0 ? .7 : 1;
            var topY = FrameHeight - bottomY - height;
            for (var y = Math.Max(0, topY); y < Math.Min(FrameHeight, topY + height); y++)
            {
                for (var column = Math.Max(0, x); column < Math.Min(FrameWidth, x + width); column++)
                {
                    var pixel = y * FrameWidth * BgraFrame.BytesPerPixel
                        + column * BgraFrame.BytesPerPixel;
                    pixels[pixel] = Blend(blue, pixels[pixel], alpha);
                    pixels[pixel + 1] = Blend(green, pixels[pixel + 1], alpha);
                    pixels[pixel + 2] = Blend(red, pixels[pixel + 2], alpha);
                }
            }
        }
        return new BgraFrame(
            FrameWidth, FrameHeight, FrameWidth * BgraFrame.BytesPerPixel,
            pixels, captureId, DateTimeOffset.UnixEpoch.AddMilliseconds(captureId));
    }

    private static byte Blend(byte foreground, byte background, double alpha) =>
        (byte)Math.Round(foreground * alpha + background * (1 - alpha),
            MidpointRounding.AwayFromZero);
}
