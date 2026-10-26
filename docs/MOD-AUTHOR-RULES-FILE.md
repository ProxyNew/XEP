# Mod-author rules file (v1 contract)

A mod author can ship a JSON rules file to tell XEnchantmentPatcher how to treat the mod's equipment and enchantments. The file states the author's intended defaults; XEP's eligibility, availability, gate, level-window and consumer-isolation checks still decide what is possible.

This document describes the supported rules format, scan application and export.

## Location

Install loose JSON files directly in `Data/XEnchantmentPatcher/`, for example
`Data/XEnchantmentPatcher/xep_examplegear.json`. XEP reads every `.json` file
in that folder and combines their rules during Scan. Subfolders and archives
(BSA) are not read. Give each mod's file a unique name so mod managers can keep
them together; copies with the same filename still overwrite each other according
to mod-manager priority. Filenames do not select plugins: each file's `plugins`
entries identify its targets, and one file can describe several plugins.

Export suggests `xep_<plugin name>.json` (lowercase, without `.esp`/`.esm`/`.esl`) using the first selected plugin
alphabetically. You can choose another unique `.json` filename in the save dialog.

## Example

```json
{
  "schemaVersion": 1,
  "plugins": [
    {
      "plugin": "ExampleGear.esp",
      "defaults": {
        "patchPolicy": "ExistingDistribution",
        "distributionMode": "SpecializedEnchantedLists",
        "reason": "Variants belong in the mod's own enchanted lists."
      },
      "items": [
        {
          "id": "keep-relic-unique",
          "records": ["000800:ExampleGear.esp"],
          "include": false,
          "reason": "This relic is intended to remain unique."
        },
        {
          "records": ["000801:ExampleGear.esp", "000802:ExampleGear.esp"],
          "materialFamily": "Steel",
          "distributionMode": "GeneralLists"
        }
      ],
      "enchantments": [
        {
          "records": ["000900:ExampleGear.esp"],
          "enabled": false,
          "reason": "Quest-only enchantment."
        }
      ]
    }
  ]
}
```

More [sample files](rules-file-samples/) and a [JSON Schema](schemas/xep_pluginrules.schema.json) for editor support accompany this document.

## Fields

### Top level

| Field | Required | Meaning |
| --- | --- | --- |
| `schemaVersion` | yes | Integer contract version. This document defines `1`. |
| `plugins` | yes | Array of plugin entries. Each plugin may appear once; a repeated entry is not used. |

### Plugin entry

| Field | Required | Meaning |
| --- | --- | --- |
| `plugin` | yes | Plugin file name, for example `ExampleGear.esp`. The entry is dormant when that plugin is not loaded. |
| `defaults` | no | Plugin-wide defaults for the plugin's equipment (below). |
| `items` | no | Rules for exact equipment records (below). |
| `enchantments` | no | Rules for exact enchantment records (below). |

Every record listed in an entry must be defined by that entry's plugin.

### `defaults`

| Field | Values |
| --- | --- |
| `patchPolicy` | `ExistingDistribution` or `Exclude`. Leave it out to keep the plugin undecided (Review); the patcher's retired policies are not accepted. |
| `distributionMode` | Any distribution mode (below). |
| `reason` | Optional display text. |

Defaults that set neither `patchPolicy` nor `distributionMode` have no effect.

### Item rule

| Field | Required | Meaning |
| --- | --- | --- |
| `records` | yes | Non-empty array of armor or weapon FormKeys. |
| `include` | at least one of `include`, `materialFamily`, `distributionMode`, `allowedEnchantments`, `allowedDestinations` | `true` or `false`: whether the equipment is selected for variants. |
| `materialFamily` | | One of the material families XEP offers for that item (for example `Steel`). |
| `distributionMode` | | Any distribution mode (below). |
| `allowedEnchantments` | | Non-empty array of enchantment FormKeys from any plugin. Variants of these records use only the listed enchantments; listing a family's base enchantment allows every tier of that family. It only narrows: an enchantment the user disabled stays disabled. If nothing compatible remains, the item gets no variants. The user can ignore the list per item. A rule using it must list `"allowedEnchantments"` in `requires`. |
| `allowedDestinations` | | Non-empty array of leveled item list or container FormKeys from any plugin. Variants of these records are placed only in or below the named lists (a named list covers every list beneath it) or into the named containers (a container also covers the lists placed directly in it). It only narrows routes XEP already accepts: the destination's gates, level windows and consumers still apply, and XEP still writes beside or into its own copies rather than editing the named list. If no accepted route is left, the item gets no variants; there is no fallback to other lists, merchants or general loot. The user can ignore the list per item. A rule using it must list `"allowedDestinations"` in `requires`. |
| `id` | no | Short name used in diagnostics. Without it, diagnostics name the record. |
| `reason` | no | Display text shown to users. Never interpreted. |
| `requires` | no | Array of feature names the rule depends on (see Compatibility). |

### Enchantment rule

| Field | Required | Meaning |
| --- | --- | --- |
| `records` | yes | Non-empty array of enchantment FormKeys. |
| `enabled` | yes | Whether the enchantment is used for generated variants by default. |
| `id`, `reason`, `requires` | no | As for item rules. |

### Record identifiers

Records are FormKeys: six hexadecimal digits of the local form ID, a colon and the defining plugin, for example `000800:ExampleGear.esp`. Plugin names are not case-sensitive. They do not depend on load order. EditorIDs, keywords and name patterns are not selectors.

### Distribution modes

`Disabled`, `SpecializedLists`, `GeneralLists`, `BestAvailable`, `SpecializedEnchantedLists`, `MerchantFallback`, `ScriptDistribution`. These are the patcher's current distribution choices; its retired choices (`OtherMaterialLists`, `RuntimeMirror`) are not accepted. Mixing modes (such as `SpecializedLists`, `GeneralLists` and `MerchantFallback`) are allowed: an installed rules file counts as the user's selection of the mixing modes it requests. Placement still stays with compatible recipients.

## How rules apply

1. XEP's engine requirements come first. A rule can request a choice; it cannot make an ineligible item eligible or create a route the load order does not support.
2. Enchantment overlap recommendations outrank the rules file: `enabled: true` cannot re-enable an enchantment that XEP turned off because it overlaps a higher-priority origin's enchantment. (`enabled: false` still applies.)
3. Item rules override the plugin's `defaults`.
4. Author values fill every choice the user left on Review or on its default, including mixing modes.
5. When an author value differs from a choice the user set explicitly, XEP lists the conflict when the file is loaded. The user can keep all their choices or overwrite all of them for the file, or decide each item and field. XEP remembers each kept choice together with both values, so it asks again only when the author's value or the user's value changes.
6. Overwriting removes the user's own choice so the author value applies. Author values are never saved as the user's settings: removing the file returns those choices to XEP's recommendations.

Two exact-record rules that set the same field of the same record to different
values are both invalid, whether they are in one file or separate files. Each
conflicting rule is skipped completely. Rules that agree are fine. When files
disagree on a plugin's default patch policy or distribution mode, the conflicting
plugin default blocks are skipped completely; their exact-record rules can still
apply. Nonconflicting files combine their defaults and rules. Exact-record rules
override plugin defaults regardless of filename order. Diagnostics identify each
affected file; filename order never resolves conflicting values.

## Compatibility

Files are meant to keep working across XEP versions.

- `schemaVersion` changes only for a breaking redesign. A file with a version this XEP does not support is an error and is not used; update the file or XEP.
- New fields are added as optional fields, which an older XEP ignores, or as required fields. A rule that depends on a newer field lists its feature name in `requires`. An XEP that does not know a listed feature leaves that rule unused and reports that the file needs a newer XEP; the rest of the file still applies.
- Known feature names: `allowedEnchantments`, `allowedDestinations`. An XEP that predates a feature leaves rules requiring it dormant.
- Unknown property names are reported as warnings (usually a typo) and never block the file.

## Errors

Each rule applies completely or not at all.

- A file that is not valid JSON, or whose `schemaVersion` is missing or unsupported, is not used.
- A rule with an unresolved or wrong-type record, a record from another plugin, an invalid value, an unknown `requires` feature or no setting is not used. Other rules still apply.
- Two rules that set the same field of the same record to different values are both not used.
- A repeated plugin entry is not used; the first entry for that plugin still applies.
- A plugin entry for a plugin that is not loaded is dormant.

Every problem is shown as a warning, and patching continues as if the unused file or rule were absent. A file larger than 1 MiB, or listing more than 10,000 records (every `records` entry counts, across all rules), is not used.

## Checking installed files

Install the file in your mod (for example at
`Data/XEnchantmentPatcher/xep_examplegear.json`). During an interactive Scan,
XEnchantmentPatcher reads the loose JSON files in that folder, checks their
rules against the loaded plugins and records, and reports problems in the scan
warnings. Conflicts between files are reported for each affected file. A
malformed or unreadable file does not stop other files from applying. The
command-line scans and previews ignore rules files.
