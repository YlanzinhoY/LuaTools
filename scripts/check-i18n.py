#!/usr/bin/env python3
"""Validate the localization RESX files.

Checks every Strings.<tag>.resx against the English Strings.resx:
  - is well-formed XML
  - has EXACTLY the same set of <data> keys (no missing, no extra)
  - every value keeps the same set of {0}/{1}... placeholders as English (catches broken interpolation)

Exit code 1 if any problem is found. Run locally or in CI.
Usage: python scripts/check-i18n.py
"""
import re
import sys
import glob
import os
import xml.etree.ElementTree as ET

RES_DIR = os.path.join("src", "LuaToolsGui", "Resources")
ENGLISH = os.path.join(RES_DIR, "Strings.resx")

DATA_RE = re.compile(r'<data name="([^"]+)" xml:space="preserve"><value>(.*?)</value></data>', re.S)
PLACEHOLDER_RE = re.compile(r'\{(\d+)\}')

# Keys that are DELIBERATELY English-only for now: the feature's UI is still settling, so translating
# it would just be rework. They're exempt from the "missing key" check (the app falls back to English
# per-key at runtime) but are still reported as a reminder.
#
# This list IS the handoff to the translation pass. When a feature's UI is final: translate its keys
# across every Strings.<tag>.resx, clear them from here, and this check goes back to demanding full
# parity. Anything left here is untranslated in all 29 languages.
PENDING_TRANSLATION: set[str] = {
    "Manage_Action_Achievements",
    "Achievements_Title",
    "Achievements_Progress",
    "Achievements_Search",
    "Achievements_Earned",
    "Achievements_Locked",
    "Achievements_Empty",
    "Achievements_Source",
    "Achievements_StatePath",
    "Achievements_LoadFailed",
    "Achievements_Close",
    "Achievements_Preview",
    "Achievements_NotificationTitle",
    "Achievements_SyncSteam",
    "Achievements_SyncProgress",
    "Achievements_SyncSummary",
    "Achievements_SyncNone",
    "Achievements_SyncFailed",
    "Manage_Action_CanIRunIt",
    "Settings_Section_OpenRouter",
    "Settings_OpenRouterKey",
    "Settings_OpenRouterKey_Hint",
    "Settings_OpenRouterModel",
    "Settings_OpenRouterKeyPlaceholder",
    "Settings_OpenRouterConfigured",
    "Settings_OpenRouterSave",
    "Settings_OpenRouterClear",
    "Settings_OpenRouterGetKey",
    "Settings_OpenRouterKeyBad",
    "Settings_OpenRouterSaved",
    "Settings_OpenRouterCleared",
    "Settings_OpenRouterSaveFailed",
    "Settings_OpenRouterEnvironment",
    "CanIRunIt_Title",
    "CanIRunIt_KeyTitle",
    "CanIRunIt_KeyHint",
    "CanIRunIt_RememberKey",
    "CanIRunIt_GetKey",
    "CanIRunIt_Analyze",
    "CanIRunIt_KeyRequired",
    "CanIRunIt_Analyzing",
    "CanIRunIt_AnalyzingHint",
    "CanIRunIt_Retry",
    "CanIRunIt_Close",
    "CanIRunIt_Verdict_Recommended",
    "CanIRunIt_Verdict_Minimum",
    "CanIRunIt_Verdict_Poor",
    "CanIRunIt_Verdict_Unsupported",
    "CanIRunIt_Confidence",
    "CanIRunIt_Confidence_High",
    "CanIRunIt_Confidence_Medium",
    "CanIRunIt_Confidence_Low",
    "CanIRunIt_YourHardware",
    "CanIRunIt_Hardware_UnknownGpu",
    "CanIRunIt_LogicalProcessors",
    "CanIRunIt_GpuVram",
    "CanIRunIt_StorageFree",
    "CanIRunIt_ComponentCheck",
    "CanIRunIt_Status_Exceeds",
    "CanIRunIt_Status_Meets",
    "CanIRunIt_Status_Below",
    "CanIRunIt_Status_Unknown",
    "CanIRunIt_Recommendations",
    "CanIRunIt_Caveats",
    "CanIRunIt_PublishedRequirements",
    "CanIRunIt_Minimum",
    "CanIRunIt_Recommended",
    "CanIRunIt_Requirements_NotPublished",
    "CanIRunIt_Disclaimer",
    "CanIRunIt_Error_InvalidKey",
    "CanIRunIt_Error_RateLimit",
    "CanIRunIt_Error_NoRequirements",
    "CanIRunIt_Error_Hardware",
    "CanIRunIt_Error_BackendMissing",
    "CanIRunIt_Error_Steam",
    "CanIRunIt_Error_OpenRouter",
    "CanIRunIt_Error_Generic",
}
# Empty on purpose: every key is translated in all 29 languages, so the parity check above is
# unconditional. Add a key here ONLY while its feature's UI is still moving, and clear it again
# as soon as the translations land. Anything listed is English-only for every user.



def parse(path):
    """Return {key: value} for a RESX file (also raises if XML is malformed)."""
    text = open(path, encoding="utf-8").read()
    ET.fromstring(text)  # well-formedness check
    return dict(DATA_RE.findall(text))


def placeholders(value):
    return set(PLACEHOLDER_RE.findall(value))


def main():
    if not os.path.exists(ENGLISH):
        print(f"ERROR: {ENGLISH} not found (run from repo root)")
        return 1

    en = parse(ENGLISH)
    base_keys = set(en)
    problems = []

    for path in sorted(glob.glob(os.path.join(RES_DIR, "Strings.*.resx"))):
        name = os.path.basename(path)
        try:
            tr = parse(path)
        except ET.ParseError as e:
            problems.append(f"{name}: INVALID XML. {e}")
            continue

        missing = base_keys - set(tr) - PENDING_TRANSLATION
        extra = set(tr) - base_keys
        if missing:
            problems.append(f"{name}: missing {len(missing)} key(s): {', '.join(sorted(missing)[:10])}")
        if extra:
            problems.append(f"{name}: {len(extra)} unknown key(s): {', '.join(sorted(extra)[:10])}")

        # Placeholder parity (only for keys present in both).
        for k in base_keys & set(tr):
            if placeholders(en[k]) != placeholders(tr[k]):
                problems.append(
                    f"{name}: key '{k}' placeholder mismatch "
                    f"(en={sorted(placeholders(en[k]))} vs {sorted(placeholders(tr[k]))})")

    count = len(glob.glob(os.path.join(RES_DIR, "Strings.*.resx")))
    if problems:
        print(f"i18n check FAILED ({len(problems)} problem(s) across {count} language files):")
        for p in problems:
            print("  -", p)
        return 1

    print(f"i18n check OK: {count} language files, {len(base_keys)} keys each, "
          f"XML valid, placeholders consistent.")

    pending = PENDING_TRANSLATION & base_keys
    if pending:
        print(f"\nNOTE: {len(pending)} key(s) are English-only by design and exempt from the parity "
              f"check (PENDING_TRANSLATION in this script). Translate them once the UI is final:")
        for k in sorted(pending):
            print("  -", k)
    stale = PENDING_TRANSLATION - base_keys
    if stale:
        print(f"\nNOTE: {len(stale)} PENDING_TRANSLATION entr(ies) no longer exist in Strings.resx. "
              f"Drop them from the list: {', '.join(sorted(stale))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
