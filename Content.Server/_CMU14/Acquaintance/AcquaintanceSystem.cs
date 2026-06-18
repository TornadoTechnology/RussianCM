using Content.Server.Chat.Systems;
using Content.Server.Humanoid;
using Content.Shared.Chat;
using Content.Shared.Examine;
using Content.Shared.Ghost;
using Content.Shared.Humanoid;
using Content.Shared.IdentityManagement;
using Content.Shared.IdentityManagement.Components;
using Content.Shared.Mind;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Robust.Shared.Utility;

using Robust.Shared.Enums;

namespace Content.Server._CMU14.Acquaintance;

/// <summary>
/// Server-authoritative recognition of faces and voices.
/// </summary>
public sealed partial class AcquaintanceSystem : EntitySystem
{
    [Dependency] private SharedMindSystem _mind = default!;
    [Dependency] private HumanoidAppearanceSystem _humanoid = default!;
    [Dependency] private SharedPopupSystem _popup = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<GetVerbsEvent<InteractionVerb>>(OnGetInteractionVerbs);
    }

    private void OnGetInteractionVerbs(GetVerbsEvent<InteractionVerb> args)
    {
        if (args.User == args.Target ||
            !args.CanAccess ||
            !args.CanInteract ||
            !HasComp<HumanoidAppearanceComponent>(args.User) ||
            !HasComp<HumanoidAppearanceComponent>(args.Target) ||
            !_mind.TryGetMind(args.User, out _, out _) ||
            !_mind.TryGetMind(args.Target, out _, out _))
        {
            return;
        }

        args.Verbs.Add(new InteractionVerb
        {
            Text = Loc.GetString("acquaintance-introduce-verb"),
            Message = Loc.GetString("acquaintance-introduce-verb-description"),
            Icon = new SpriteSpecifier.Texture(new ResPath("/Textures/Interface/VerbIcons/bubbles.svg.192dpi.png")),
            Priority = 1,
            Act = () => Introduce(args.User, args.Target)
        });
    }

    public void Introduce(EntityUid speaker, EntityUid listener)
    {
        if (!_mind.TryGetMind(listener, out var listenerMindId, out _))
            return;

        if (!TryComp(listenerMindId, out AcquaintanceComponent? memory))
            memory = EnsureComp<AcquaintanceComponent>(listenerMindId);

        var claimedName = Identity.Name(speaker, EntityManager, listener).Name;
        var unknownFace = GetUnknownFaceDescription(speaker);
        var voiceName = GetTransformedVoiceName(speaker);
        var faceVisible = CanSeeFace(speaker);

        if (faceVisible)
            memory.KnownFaces[speaker] = claimedName;

        memory.KnownVoices[GetVoiceSignature(speaker, voiceName)] = claimedName;

        _popup.PopupEntity(
            Loc.GetString("acquaintance-introduce-speaker", ("target", GetPerceivedFaceName(speaker, listener)), ("name", claimedName)),
            speaker,
            speaker,
            PopupType.Medium);

        _popup.PopupEntity(
            Loc.GetString(
                faceVisible ? "acquaintance-introduce-listener-face" : "acquaintance-introduce-listener-voice",
                ("speaker", unknownFace),
                ("name", claimedName)),
            speaker,
            listener,
            PopupType.Medium);
    }

    public string GetPerceivedFaceName(EntityUid viewer, EntityUid target)
    {
        if (viewer == target || HasComp<GhostComponent>(viewer))
            return Identity.Name(target, EntityManager, viewer).Name;

        if (!HasComp<HumanoidAppearanceComponent>(target))
            return Identity.Name(target, EntityManager, viewer).Name;

        if (!CanSeeFace(target))
            return GetUnknownFaceDescription(target);

        if (TryGetMemory(viewer, out var memory) &&
            memory.KnownFaces.TryGetValue(target, out var knownName))
        {
            return knownName;
        }

        return GetUnknownFaceDescription(target);
    }

    public string GetPerceivedVoiceName(EntityUid viewer, EntityUid speaker, string transformedVoiceName)
    {
        if (viewer == speaker || HasComp<GhostComponent>(viewer))
            return transformedVoiceName;

        if (TryGetMemory(viewer, out var memory) &&
            memory.KnownVoices.TryGetValue(GetVoiceSignature(speaker, transformedVoiceName), out var knownName))
        {
            return knownName;
        }

        return GetUnknownVoiceDescription(speaker, transformedVoiceName);
    }

    public string GetUnknownFaceDescription(EntityUid target)
    {
        if (!TryComp(target, out HumanoidAppearanceComponent? appearance))
            return Loc.GetString("acquaintance-unknown-person");

        var age = _humanoid.GetAgeRepresentation(appearance.Species, appearance.Age);
        var gender = appearance.Gender switch
        {
            Gender.Female => Loc.GetString("identity-gender-feminine"),
            Gender.Male => Loc.GetString("identity-gender-masculine"),
            _ => Loc.GetString("identity-gender-person")
        };

        return Loc.GetString("acquaintance-unknown-face", ("gender", gender), ("age", age));
    }

    private string GetUnknownVoiceDescription(EntityUid speaker, string transformedVoiceName)
    {
        // A renamed voice is treated as electronically or otherwise altered, so its
        // owner's physical characteristics are not leaked through the description.
        if (!string.Equals(transformedVoiceName, Name(speaker), StringComparison.Ordinal) ||
            !TryComp(speaker, out HumanoidAppearanceComponent? appearance))
        {
            return Loc.GetString("acquaintance-unknown-voice");
        }

        return appearance.Gender switch
        {
            Gender.Female => Loc.GetString("acquaintance-unknown-voice-feminine"),
            Gender.Male => Loc.GetString("acquaintance-unknown-voice-masculine"),
            _ => Loc.GetString("acquaintance-unknown-voice")
        };
    }

    private bool TryGetMemory(EntityUid character, out AcquaintanceComponent memory)
    {
        if (_mind.TryGetMind(character, out var mindId, out _) &&
            TryComp(mindId, out AcquaintanceComponent? component))
        {
            memory = component;
            return true;
        }

        memory = default!;
        return false;
    }

    private bool CanSeeFace(EntityUid target)
    {
        var ev = new SeeIdentityAttemptEvent();
        RaiseLocalEvent(target, ev);
        return !ev.Cancelled;
    }

    private string GetTransformedVoiceName(EntityUid speaker)
    {
        var ev = new TransformSpeakerNameEvent(speaker, Name(speaker));
        RaiseLocalEvent(speaker, ev);
        return ev.VoiceName;
    }

    private static string GetVoiceSignature(EntityUid speaker, string transformedVoiceName)
    {
        return $"{speaker}:{transformedVoiceName.Trim().ToUpperInvariant()}";
    }
}
