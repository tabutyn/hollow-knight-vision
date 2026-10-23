using static HollowKnightVision.Windows.Core.MenuStencilCatalog;

namespace HollowKnightVision.Windows.Core;

public sealed partial class MenuStencilCatalogBuilder
{
    private sealed record CorrectionTarget(string Context, string Option, string Language);
    // Exact reviewed Contract Breach IDs from MenuStencil.swift, not arbitrary
    // two-box drafts. Unreviewed correction markers cannot redefine a scene.
    private static readonly IReadOnlyDictionary<Guid, CorrectionTarget> CorrectionTargets = new Dictionary<Guid, CorrectionTarget>
    {
        [Guid.Parse("5EF5867D-16EC-4202-B9A7-0B273A4CCE12")] = new("options", "options.game", "fr"),
        [Guid.Parse("A343B40F-10B9-4992-808E-A4DF90DD60DE")] = new("game-options", "shared.back", "fr"),
        [Guid.Parse("7BDBE122-1CF4-48A0-AA47-0DF9DA22E8BD")] = new("game-options", "shared.reset-defaults", "fr"),
        [Guid.Parse("9AF0F76E-969F-4A39-BE1F-85DCBFC43FDA")] = new("extras", "extras.lifeblood", "es"),
        [Guid.Parse("BA6281F5-61BD-4FDE-A88F-84AF957A0668")] = new("extras", "extras.the-grimm-troupe", "es"),
        [Guid.Parse("62D3E3C4-A574-4946-B26D-A646EE6666AE")] = new("extras", "extras.hidden-dreams", "es"),
        [Guid.Parse("F44121EB-E362-4ADB-A314-48ECAAE61E28")] = new("keyboard", "shared.attack", "es"),
        [Guid.Parse("3ABEEF76-A4E8-4BDA-9C0C-5B7F6B503AE2")] = new("options", "shared.controller", "es"),
        [Guid.Parse("8F82F042-B421-4FC4-9BC4-A7CD23D30A52")] = new("video", "video.full-screen", "es"),
        [Guid.Parse("F1EE8587-0830-4A26-A3DA-C526267A98CE")] = new("game-options", "shared.reset-defaults", "es"),
        [Guid.Parse("FCFC1ADD-C60D-4B30-AC31-E9E42E5C020F")] = new("quit-game", "shared.no", "pt-BR"),
        [Guid.Parse("6E4C3D9F-73CA-49E4-B2F0-C30D3A48DBEE")] = new("keyboard", "shared.super-dash", "pt-BR"),
        [Guid.Parse("997CB3D1-0546-4B2A-9FD4-396DF6CDFC6D")] = new("keyboard", "shared.attack", "pt-BR"),
        [Guid.Parse("7B18DA08-52B2-40DD-904D-E68412C5DE10")] = new("keyboard", "inventory.inventory", "pt-BR"),
        [Guid.Parse("804959CD-CEDE-47EC-B5EC-99B5C1DB24BB")] = new("video", "shared.advanced-settings", "pt-BR"),
        [Guid.Parse("08B7D411-2B22-4BA1-83DD-D35519E8CC21")] = new("video", "video.full-screen", "pt-BR"),
        [Guid.Parse("23177404-3D79-4AD0-A2CE-67F6C8CA69D1")] = new("options", "options.game", "pt-BR"),
        [Guid.Parse("35F5BDEE-F9C8-4B35-95B2-40AAE1E4EB24")] = new("extras", "extras.lifeblood", "ko"),
        [Guid.Parse("577B177F-29D8-4D26-AA5C-EAD1A325841C")] = new("extras", "extras.hidden-dreams", "ko"),
        [Guid.Parse("69FA0F82-93F4-49C8-8BE1-2CDDD89748A9")] = new("extras", "extras.the-grimm-troupe", "ko"),
        [Guid.Parse("E8FC52A8-3E99-407C-9A83-1D88E7143BF5")] = new("options", "options.game", "es"),
        [Guid.Parse("04D40700-0C53-4E5D-B2D8-0DA5C134774D")] = new("options", "options.game", "fr"),
        [Guid.Parse("0D565524-8EC3-442C-B8F3-3152A0A0382A")] = new("options", "options.game", "it"),
        [Guid.Parse("01A82890-192D-4CD7-BB55-E0D4B27C3CB9")] = new("options", "shared.keyboard", "it"),
        [Guid.Parse("FC571AB9-9AB2-4EE9-BEC0-7257EFF93C1B")] = new("video", "shared.advanced-settings", "it"),
        [Guid.Parse("973AB0CA-EEF2-4902-8B53-E0765479567E")] = new("options", "options.game", "it"),
        [Guid.Parse("911E9057-3FE0-452C-BE9C-2D68AD922154")] = new("keyboard", "shared.attack", "it"),
        [Guid.Parse("AE1F5C8D-6D41-427C-B6BE-DDCC911964BD")] = new("keyboard", "shared.super-dash", "it"),
        [Guid.Parse("D57CC45F-DE6A-4BA6-A25F-FDB67B3ADEEA")] = new("keyboard", "inventory.inventory", "it"),
        [Guid.Parse("FA0753AB-7E1F-4024-A1EF-F5E651F51B93")] = new("options", "options.game", "ja"),
        [Guid.Parse("152AE0A8-198B-4596-A705-DED146B7883E")] = new("extras", "extras.hidden-dreams", "ja"),
        [Guid.Parse("E5616482-3FF6-4842-B313-DFB50C7783BC")] = new("extras", "extras.the-grimm-troupe", "ja"),
        [Guid.Parse("6D028D16-970D-4755-845B-34DCBF91A247")] = new("extras", "extras.lifeblood", "ja"),
        [Guid.Parse("34021979-272E-4680-84E7-2D5BDD35555C")] = new("video", "shared.advanced-settings", "ja"),
        [Guid.Parse("5393B426-F329-4E86-80D2-5E104C36A746")] = new("extras", "extras.hidden-dreams", "ja"),
        [Guid.Parse("7B00F837-FC48-46FC-9AF2-9C19C471ED3E")] = new("extras", "extras.the-grimm-troupe", "ja"),
        [Guid.Parse("07A600CE-FC9A-42FD-AA72-6E54D0412971")] = new("extras", "extras.lifeblood", "ja"),
        [Guid.Parse("D3BF03A8-98EC-4CEA-9696-B647C674221E")] = new("quit-game", "shared.no", "ja"),
        [Guid.Parse("00B9FE92-B22D-400A-947C-03848590DBBF")] = new("video", "shared.advanced-settings", "ja"),
        [Guid.Parse("AA6813FA-8B96-4B2C-B499-0A3F46F745C6")] = new("keyboard", "shared.attack", "ko"),
        [Guid.Parse("3224CB73-47A1-4F4B-B4AD-21EE272B4B81")] = new("keyboard", "shared.attack", "ko"),
        [Guid.Parse("16A25BAA-201A-40AD-ABC2-28F97683D505")] = new("extras", "extras.lifeblood", "ko"),
        [Guid.Parse("144A8CFB-9C77-4D92-A621-D7780B96335F")] = new("extras", "extras.the-grimm-troupe", "ko"),
        [Guid.Parse("66CCC048-781A-4D48-8D80-6D58A1CA9530")] = new("video", "shared.advanced-settings", "ko"),
        [Guid.Parse("F17C0BF8-9346-47B7-B0F2-CBCD9436E61F")] = new("keyboard", "keyboard.right", "ko"),
        [Guid.Parse("BB7C33FD-0000-41D3-A25F-525B663772D2")] = new("keyboard", "shared.super-dash", "ko"),
    };

    private IReadOnlyList<Scene> ApplyCorrections(IReadOnlyList<SavedLabelingExample> examples, IReadOnlyList<Scene> source, int width, int height)
    {
        var scenes = source.ToList();
        foreach (var example in examples.OrderBy(example => example.Manifest.CreatedAt))
        {
            if (!CorrectionTargets.TryGetValue(example.Id, out var target) || example.Manifest.ImageWidth != width || example.Manifest.ImageHeight != height) continue;
            var sceneIndex = scenes.FindIndex(scene => scene.ContextIdentifier == target.Context);
            if (sceneIndex < 0) continue;
            var scene = scenes[sceneIndex]; var options = scene.Options.ToList();
            var optionIndex = options.FindIndex(option => option.ClassIdentifier == target.Option);
            if (optionIndex < 0 || LoadPixels(example.ImagePath, width, height) is not { } pixels) continue;
            var annotations = Selectors(example).Select(annotation => PixelBounds(annotation, width, height)).OrderBy(rect => rect.MidX).ToArray();
            if (annotations.Length == 0) continue;
            var option = options[optionIndex];
            StencilRect? left = null, right = null;
            if (annotations.Length >= 2) { left = annotations[0]; right = annotations[^1]; }
            else if (option.LeftSelector is { } previousLeft && option.RightSelector is { } previousRight)
            {
                if (Math.Abs(annotations[0].MidX - previousLeft.BoundsFor(target.Language).MidX)
                    <= Math.Abs(annotations[0].MidX - previousRight.BoundsFor(target.Language).MidX)) left = annotations[0];
                else right = annotations[0];
            }
            PositionedSelector? Correct(PositionedSelector? selector, StencilRect? bounds)
            {
                if (selector is null || bounds is null) return selector;
                var localized = selector.LocalizedBounds?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
                localized[target.Language] = bounds.Value;
                var variants = selector.Variants.ToList();
                if (pixels.Reference(bounds.Value) is { } reference) variants.Add(MakeVariant($"human-selector-correction.{target.Language}", reference, language: target.Language));
                return selector with { LocalizedBounds = localized, Variants = variants };
            }
            options[optionIndex] = option with { LeftSelector = Correct(option.LeftSelector, left), RightSelector = Correct(option.RightSelector, right) };
            scenes[sceneIndex] = scene with { Options = options };
        }
        return scenes;
    }

    private static IReadOnlyList<MenuSelectorCorrection> CorrectionPlacements(IEnumerable<SavedLabelingExample> examples, IReadOnlyList<Scene> scenes)
    {
        var placements = new Dictionary<string, MenuSelectorCorrection>(StringComparer.Ordinal);
        foreach (var example in examples.OrderBy(example => example.Manifest.CreatedAt))
        {
            if (!CorrectionTargets.TryGetValue(example.Id, out var target)) continue;
            var option = scenes.FirstOrDefault(scene => scene.ContextIdentifier == target.Context)?.Options.FirstOrDefault(option => option.ClassIdentifier == target.Option);
            if (option?.LeftSelector is not { } left || option.RightSelector is not { } right) continue;
            placements[$"{target.Context}:{target.Option}:{target.Language}"] = new(target.Context, target.Option, target.Language,
                left.BoundsFor(target.Language), right.BoundsFor(target.Language), example.Id);
        }
        return placements.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToArray();
    }
}
