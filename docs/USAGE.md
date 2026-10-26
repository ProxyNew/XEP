# XEP 1.0.0 usage guide

## Start here

1. Run `XEnchantmentPatcher.exe` through Mod Organizer 2 so it sees the active profile's virtual Data folder, winning plugins and scripts. Launching without arguments opens the graphical interface.
2. Select the patcher's saved profile and verify the **Data folder** and **Output folder**.
3. Check the enchantment origins you want to use, then drag them into priority order.
4. Open **Patch review**. Choose plugin rules, include the equipment you want, review enchantments and resolve any specialized-loot decisions.
5. Calculate the exact output in **Output preview**. Read the warnings and inspect undistributed items or rejected routes.
6. Choose **Generate and validate ESPFE**, review the confirmation, and install the complete generated output folder through MO2.

Normal settings are saved under `%LOCALAPPDATA%\XEnchantmentPatcher\settings.json`. Named patcher profiles store different choices within that file; they are not separate MO2 profiles or isolated sessions.

## Choose equipment and enchantments

### Plugin rules

| Rule | Effect |
| --- | --- |
| Review | Leave the plugin's distribution decision unresolved for review. |
| Existing Distribution | Consider eligible ordinary equipment with its own leveled-list or verified insertion route. Items needing specialized routing require a separate decision. |
| Exclude | Do not generate variants for this plugin. |

Create Distribution is retired. Saved selections migrate to Existing Distribution; XEP no longer establishes ordinary base-item loot routes.

Use the plugin toolbar's **Apply to selected** action for batch rules. Individual equipment inclusion and exclusion choices remain effective when a plugin is hidden from the review views. Already-enchanted, non-playable and unsupported equipment cannot become ordinary patch sources merely through an Include override.

Weapons, armor, rings, amulets and circlets use their own type and slot checks. Clothing support remains deferred. Material recommendations appear in the family dropdown; choosing the recommendation clears a manual override, while choosing another family saves it. The evidence pane explains classification and base-item distribution.

### Origins and enchantments

Checked origins supply enchantments and templates. Their top-to-bottom order selects the preferred compatible route within the source item's observed distribution domain. If that origin has no safe route, another checked origin may supply one.

Equivalent enchantment behavior and charge are deduplicated; names alone do not establish equivalence. Ring, amulet and circlet routes remain separate. A selected origin does not authorize unrelated consumers or content domains.

An enchantment tier defaults to enabled when it has a safe ordinary-equipment template already represented in a leveled list. Usable tiers without that distribution evidence can be enabled explicitly. Saved choices remain per tier; **Defaults for selected** and **Restore defaults** restore each tier's own default. Disabling an enchantment affects XEP's generated variants, not existing items from other mods.

## Choose specialized distribution

Specialized loot covers Existing Distribution items whose routes need a separate choice. The recommendation is marked inside each row's dropdown. Supported enchanted routes recommend **Specialized enchanted lists only**; unresolved cases recommend **Off**. Selecting a mode saves an override. **Use recommendations for selected rows** clears it so later scans can choose again.

| Choice | Result and scope |
| --- | --- |
| Off | Generate no variants for this source through specialized distribution. Existing items from other mods remain available. |
| Specialized enchanted lists only | Use supported enchanted destinations with compatible material, type, source availability and consumer context. |
| Specialized source lists | Add an enchanted family to supported source lists. This explicitly permits ordinary/enchanted mixing. |
| Best available | Try specialized enchanted routes, then source lists, then general enchanted loot. Stop at the first productive route family; inspect the reported fallback. |
| General enchanted loot (expert) | Permit compatible provider routing that can reach consumers without the ordinary source item. Available per row, excluded from batch choices, and reported with a warning. |
| Merchant fallback (otherwise undistributed) | Create occasional enchanted stock in compatible active merchant chests. Preserve one complete proven source control path; quest unlocks, unsupported paths and ambiguous alternatives remain blocked. |

A mode choice also requires the item to be included, its plugin to be enabled and its normal eligibility checks to pass. Accepted modes can still leave some combinations undistributed. The selected-item pane shows concrete destinations, source controls, reasons and the last exact-preview result; **Show full evidence** retains the detailed paths.

**Allow new material lists for specialized enchanted distribution** lets the enchanted-only mode add a supported missing material family within a compatible enchanted context. It defaults to enabled for new profiles; explicit saved opt-outs remain effective. It does not enable Off items or bypass subtype, availability, level-window or consumer-isolation checks.

Merchant matching uses winning buy/sell records. Inclusive filters accept matching equipment keywords; exclusion filters reverse that test. Compatible stock includes a separate 50% Chance None root. Proven source globals and fixed chance controls remain independent. Adding variants or families can change selection shares; unchanged drop frequencies are not promised.


## Optional settings

- **Broader fallback for unmatched equipment:** opt in to additional source-gated enchanted routes when exact matching is exhausted. Supported subtype/slot, availability, domain and isolation checks still apply.
- **Resolve leveled-list conflicts:** recover unaware omissions from ordinary weapon/armor lists using dependency-aware entry merging. This is disabled by default. The winning record's other fields remain authoritative; enchanted provider structures and unrelated lists are excluded. Review recovered entries in the preview and report.
- **Keep undistributed variants in output:** write otherwise valid equipment records without a safe loot destination. This is disabled by default. Retained records count toward output capacity and remain explicitly undistributed; this option creates no loot route.
- **Warn at list size:** choose the warning threshold for large output lists. Output capacity validation still applies.

## Preview, rescans and output

Scan-dependent edits trigger a debounced rescan, including edits made while Patch Review is open. Changes arriving during a scan are scheduled afterward. Changing review tabs does not trigger a rescan. Changing material expansion refreshes recommendations; explicit saved overrides remain effective. Rescans reuse the plugins loaded by the session's first scan. After changing mods, restart the patcher; changing the Data folder or settings profile also reloads them.

The exact preview is **not calculated**, **current**, or **out of date**. Changed output inputs invalidate it; stale details remain visible for comparison. Calculate and Generate reuse a current preview. Successful generation invalidates it because the existing output has changed. Search, sorting and row selection do not invalidate it.

Output files are named `XEnchantmentPatcher.esp`, or `XEnchantmentPatcher_0.esp`, `XEnchantmentPatcher_1.esp` and so on. Each ESPFE has at most 4,096 new records. Load numbered outputs in numeric order and after the input plugins they override. Install the **whole generated folder**, including any `Scripts` and `SEQ` companions.

Generation validates references, master dependencies, light-record capacity, leveled-list limits and required reachability. Supported oversized lists use level-preserving helpers; a route that cannot be represented safely can be withheld instead. Previous output is backed up before replacement. The JSON report records generated/reused/undistributed decisions, destinations, list impacts, warnings and rejection evidence.

New variants without a supported destination are normally omitted from the plugins. **Keep undistributed variants in output** changes serialization only. Reused records stay in their original plugins.


## Availability and save considerations

Generated routes retain supported source globals, fixed Chance None controls, quest triggers, level windows and consumer scope. Sequential controls on a proven route remain separate; ambiguous alternatives are not guessed into an AND/OR relationship. Compatible destination structure is still required even when a source quest trigger is known.

Verified quest insertions use a companion controller that waits for the source trigger, inserts its branch and stops. Already-completed source stages can be caught up on first installation. Missing script assets or unsupported control flow protect affected routes rather than making them unconditional. Arbitrary Papyrus/SKSE behavior is not automatically understood. Supported Revert protection has limits that depend on the original script control flow.

Run through MO2 so winning loose scripts and associated archives are visible. Script analysis uses the installed Champollion executable, found in a nearby `Champollion` folder or through `XEP_CHAMPOLLION_PATH`. Skyrim needs the generated companion scripts, not the decompiler.

Generated runtime state can persist in saves. Regeneration can change record identities; a new patch is not an automatic migration of an already-used runtime patch. Read fingerprint warnings and retain an appropriate backup or pre-install save. Static output validation does not establish every quest, MCM, save/reload or in-game lifecycle behavior.

## Command-line use

Run command-line operations through MO2 with the same input visibility as the GUI. Ordinary command-line generation can write output and save settings. For private development runs, use separate settings and output paths; `--help` lists the isolation options supported by the current executable.

| Option | Purpose |
| --- | --- |
| `--scan`, `--scan-json PATH` | Scan; optionally write detailed scan data. |
| `--preview` | Calculate exact output without writing plugins. |
| `--data PATH`, `--output-folder PATH`, `--profile NAME` | Select input, output and a saved patcher profile. |
| `--provider PLUGIN` | Enable an origin; repeat in highest-to-lowest priority order. |
| `--providers` | List available enchantment origins. |
| `--existing PLUGIN`, `--create-distribution PLUGIN`, `--exclude PLUGIN` | Set a plugin's equipment policy. |
| `--include-item FORMKEY`, `--exclude-item FORMKEY` | Set a per-item inclusion override. |
| `--material-expansion BOOL`, `--specialized-enchanted FORMKEY` | Set specialized material expansion or enchanted-only routing. |
| `--broad-unmatched BOOL`, `--resolve-leveled-list-conflicts BOOL` | Set optional fallback or conflict recovery. |
| `--keep-undistributed BOOL`, `--warning-list-size 1-255` | Set output retention or list warnings. |
| `--no-save`, `--log PATH` | Keep saved settings unchanged or redirect logging. `--no-save` alone does not isolate the output. |
| `--help` | Show the options supported by the executable you are using. |

Legacy `--all` and `--general` are aliases for `--existing`; `--thaumaturgy` aliases `--provider`. Recognized subtype enforcement is always active; the retired subtype setting is not a way to bypass it.

