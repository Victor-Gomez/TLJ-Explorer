using System.IO;
using TLJExplorer.Core.FileSystem;

namespace TLJExplorer.Core.Formats;

/// <summary>
/// Heuristic matching between 3D models (<c>.cir</c>), animations (<c>.ani</c>), and skins/textures
/// (<c>.tm</c>). When opening an animation or model in a scene folder with multiple characters
/// (e.g. <c>\12\00\</c> with Charles and Emma), resolves the appropriate sibling model, skin, and
/// animation based on character name stems, prefixes, and model material texture references.
/// </summary>
public static class ModelAssetMatcher
{
    /// <summary>
    /// Finds the best matching <c>.cir</c> model for the given animation file <paramref name="aniNode"/>.
    /// Prefers models whose name matches the animation stem/prefix (e.g. <c>emma_talk.ani</c> -> <c>emma.cir</c>).
    /// </summary>
    /// <param name="aniNode">The animation node being opened.</param>
    /// <param name="candidateModels">Candidate model nodes to select from.</param>
    /// <param name="fallbackToFirst">Whether to fall back to the first candidate if no name match scores > 0.</param>
    public static FsNode? FindMatchingModel(FsNode aniNode, IEnumerable<FsNode> candidateModels, bool fallbackToFirst = true)
    {
        ArgumentNullException.ThrowIfNull(aniNode);
        if (candidateModels is null) return null;

        string aniStem = Path.GetFileNameWithoutExtension(aniNode.Name);
        List<FsNode> candidates = candidateModels.ToList();
        if (candidates.Count == 0) return null;

        FsNode? bestMatch = null;
        int bestScore = -1;

        foreach (FsNode model in candidates)
        {
            string modelStem = Path.GetFileNameWithoutExtension(model.Name);
            int score = ScoreModelToAnimationMatch(modelStem, aniStem);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = model;
            }
        }

        if (bestScore > 0)
            return bestMatch;

        return fallbackToFirst ? candidates[0] : null;
    }

    /// <summary>
    /// Finds the best default <c>.ani</c> animation for the given model <paramref name="cirNode"/>.
    /// Prefers animations whose name matches the model stem/prefix (e.g. <c>emma.cir</c> -> <c>emma_idle.ani</c> or <c>emma_talk.ani</c>).
    /// </summary>
    /// <param name="cirNode">The model node being opened.</param>
    /// <param name="candidateAnimations">Candidate animation nodes to select from.</param>
    /// <param name="fallbackToFirst">Whether to fall back to the first candidate if no name match scores > 0.</param>
    public static FsNode? FindMatchingAnimation(FsNode cirNode, IEnumerable<FsNode> candidateAnimations, bool fallbackToFirst = true)
    {
        ArgumentNullException.ThrowIfNull(cirNode);
        if (candidateAnimations is null) return null;

        string modelStem = Path.GetFileNameWithoutExtension(cirNode.Name);
        List<FsNode> candidates = candidateAnimations.ToList();
        if (candidates.Count == 0) return null;

        FsNode? bestMatch = null;
        int bestScore = -1;

        foreach (FsNode ani in candidates)
        {
            string aniStem = Path.GetFileNameWithoutExtension(ani.Name);
            int score = ScoreAnimationToModelMatch(aniStem, modelStem);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = ani;
            }
        }

        if (bestScore > 0)
            return bestMatch;

        return fallbackToFirst ? candidates[0] : null;
    }

    /// <summary>
    /// Finds the best matching <c>.tm</c> skin for the given model <paramref name="cirNode"/> and <paramref name="model"/>.
    /// Prefers skins referenced by the model's materials (e.g. material texture name <c>emma.tm</c>),
    /// or matching the model stem (e.g. <c>emma.cir</c> -> <c>emma.tm</c>).
    /// </summary>
    /// <param name="cirNode">The model node being opened.</param>
    /// <param name="model">The decoded model geometry and material data (optional).</param>
    /// <param name="candidateSkins">Candidate skin nodes to select from.</param>
    /// <param name="fallbackToFirst">Whether to fall back to the first candidate if no name match scores > 0.</param>
    public static FsNode? FindMatchingSkin(FsNode cirNode, CirModel? model, IEnumerable<FsNode> candidateSkins, bool fallbackToFirst = true)
    {
        ArgumentNullException.ThrowIfNull(cirNode);
        if (candidateSkins is null) return null;

        string modelStem = Path.GetFileNameWithoutExtension(cirNode.Name);
        List<FsNode> candidates = candidateSkins.ToList();
        if (candidates.Count == 0) return null;

        // Collect requested texture names and stems from the model's materials.
        HashSet<string> requestedTextureNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> requestedTextureStems = new(StringComparer.OrdinalIgnoreCase);
        if (model is not null)
        {
            foreach (CirMaterial mat in model.Materials)
            {
                if (!string.IsNullOrWhiteSpace(mat.TextureName))
                {
                    requestedTextureNames.Add(mat.TextureName);
                    requestedTextureStems.Add(Path.GetFileNameWithoutExtension(mat.TextureName));
                }
            }
        }

        FsNode? bestMatch = null;
        int bestScore = -1;

        foreach (FsNode skin in candidates)
        {
            string skinName = skin.Name;
            string skinStem = Path.GetFileNameWithoutExtension(skinName);

            int score = ScoreSkinMatch(skinName, skinStem, modelStem, requestedTextureNames, requestedTextureStems);
            if (score > bestScore)
            {
                bestScore = score;
                bestMatch = skin;
            }
        }

        if (bestScore > 0)
            return bestMatch;

        return fallbackToFirst ? candidates[0] : null;
    }

    private static int ScoreModelToAnimationMatch(string modelStem, string aniStem)
    {
        if (string.Equals(modelStem, aniStem, StringComparison.OrdinalIgnoreCase))
            return 1000;

        // Prefix match with delimiter (e.g. aniStem = "emma_talk", modelStem = "emma")
        if (aniStem.StartsWith(modelStem + "_", StringComparison.OrdinalIgnoreCase) ||
            aniStem.StartsWith(modelStem + "-", StringComparison.OrdinalIgnoreCase))
        {
            return 500 + modelStem.Length;
        }

        // First token of animation matches model stem
        string[] aniTokens = aniStem.Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (aniTokens.Length > 0 && string.Equals(aniTokens[0], modelStem, StringComparison.OrdinalIgnoreCase))
        {
            return 400 + modelStem.Length;
        }

        // Prefix match without delimiter (e.g. "emma1talk")
        if (aniStem.StartsWith(modelStem, StringComparison.OrdinalIgnoreCase))
        {
            return 300 + modelStem.Length;
        }

        // Suffix match (e.g. "talk_emma")
        if (aniStem.EndsWith("_" + modelStem, StringComparison.OrdinalIgnoreCase) ||
            aniStem.EndsWith("-" + modelStem, StringComparison.OrdinalIgnoreCase))
        {
            return 200 + modelStem.Length;
        }

        // Token match anywhere
        if (aniTokens.Any(t => string.Equals(t, modelStem, StringComparison.OrdinalIgnoreCase)))
        {
            return 150 + modelStem.Length;
        }

        // General substring match
        if (aniStem.Contains(modelStem, StringComparison.OrdinalIgnoreCase))
        {
            return 100 + modelStem.Length;
        }

        return 0;
    }

    private static int ScoreAnimationToModelMatch(string aniStem, string modelStem)
    {
        int baseScore = ScoreModelToAnimationMatch(modelStem, aniStem);
        if (baseScore == 0)
            return 0;

        // Bonus for idle/walk animations over specialized or action clips
        int preferenceBonus = 0;
        string lowerAni = aniStem.ToLowerInvariant();
        if (lowerAni.Contains("idle") || lowerAni.Contains("stand") || lowerAni.Contains("wait"))
            preferenceBonus = 20;
        else if (lowerAni.Contains("walk") || lowerAni.Contains("run"))
            preferenceBonus = 15;
        else if (lowerAni.Contains("talk"))
            preferenceBonus = 10;

        return baseScore + preferenceBonus;
    }

    private static int ScoreSkinMatch(
        string skinName,
        string skinStem,
        string modelStem,
        HashSet<string> requestedTextureNames,
        HashSet<string> requestedTextureStems)
    {
        // Direct match with material TextureName (mesh's own declaration)
        if (requestedTextureNames.Contains(skinName))
            return 1000;

        if (requestedTextureStems.Contains(skinStem))
            return 900;

        // Exact match with model filename stem (e.g. emma.cir -> emma.tm)
        if (string.Equals(skinStem, modelStem, StringComparison.OrdinalIgnoreCase))
            return 800;

        // Prefix match with delimiter (e.g. emma.cir -> emma_alt.tm or vice versa)
        if (skinStem.StartsWith(modelStem + "_", StringComparison.OrdinalIgnoreCase) ||
            skinStem.StartsWith(modelStem + "-", StringComparison.OrdinalIgnoreCase))
        {
            return 600 + modelStem.Length;
        }

        if (modelStem.StartsWith(skinStem + "_", StringComparison.OrdinalIgnoreCase) ||
            modelStem.StartsWith(skinStem + "-", StringComparison.OrdinalIgnoreCase))
        {
            return 500 + skinStem.Length;
        }

        // Substring match
        if (skinStem.Contains(modelStem, StringComparison.OrdinalIgnoreCase))
            return 400 + modelStem.Length;

        if (modelStem.Contains(skinStem, StringComparison.OrdinalIgnoreCase))
            return 300 + skinStem.Length;

        return 0;
    }
}
