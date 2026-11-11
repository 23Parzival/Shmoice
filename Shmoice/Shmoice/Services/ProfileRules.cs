using Shmoice.Models;

namespace Shmoice.Services;

/// <summary>Which of the commands matching a spoken phrase should run, given the app in front.</summary>
public static class ProfileRules
{
    /// <summary>
    /// A command in a profile only runs while one of the profile's apps is in front. If the same phrase
    /// exists in a matching profile and as an any-app command, the profile one wins, so one phrase can
    /// do different things in different apps. Returns why nothing ran when everything was filtered out.
    /// </summary>
    public static (List<VoiceCommand> toRun, string? skipReason) Choose(
        IReadOnlyList<VoiceCommand> matches, IEnumerable<Profile> allProfiles, string? app)
    {
        if (matches.Count == 0) return (new(), null);
        var profiles = allProfiles.ToList();
        var inProfile = new List<VoiceCommand>();
        var anyApp = new List<VoiceCommand>();
        foreach (var command in matches)
        {
            if (command.ProfileId == null) { anyApp.Add(command); continue; }
            var profile = profiles.FirstOrDefault(p => p.Id == command.ProfileId);
            if (profile == null) anyApp.Add(command); // profile was deleted: treat as any app
            else if (profile.Enabled && profile.Matches(app)) inProfile.Add(command);
        }
        if (inProfile.Count > 0) return (inProfile, null);
        if (anyApp.Count > 0) return (anyApp, null);

        var involved = matches.Select(c => profiles.FirstOrDefault(p => p.Id == c.ProfileId)).OfType<Profile>().Distinct().ToList();
        var where = string.Join(", ", involved.Select(p => p.Enabled ? $"{p.Name} ({string.Join(", ", p.AppNames())})" : $"{p.Name} (turned off)"));
        return (new(), $"Skipped: \"{matches[0].Phrase}\" only runs in {where}. You were in {app ?? "an unknown app"}.");
    }
}
