Scriptname XEP_QuestInsertion extends Quest

Quest Property SourceQuest Auto
Int Property SourceStage Auto
; Protected families have one controller with alternative unlocks (OR).
; Ordinary AddForm controllers continue to use SourceQuest/SourceStage.
Quest[] Property SourceQuests Auto
Int[] Property SourceStages Auto
LeveledItem Property TargetList Auto
LeveledItem Property VariantList Auto
Int Property EntryLevel Auto
; Only populated for a verified cross-mod Revert conflict. Normal routes still
; use AddForm. ESP-defined globals persist in saves without SKSE field edits.
GlobalVariable Property OriginalChance Auto
GlobalVariable Property ExpandedChance Auto
Bool inserted = False

Event OnInit()
    RegisterForSingleUpdate(1.0)
EndEvent

Event OnUpdate()
    If inserted
        Stop()
        Return
    EndIf
    If IsUnlocked()
        ; Only this controller owns this single insertion. State persists in the save.
        ; Populate the isolated selector BEFORE enabling the shared active branch.
        ; TargetList is absent on legacy static-switch controllers.
        If TargetList != None && VariantList != None
            TargetList.AddForm(VariantList, EntryLevel, 1)
        EndIf
        If OriginalChance != None && ExpandedChance != None
            ; Disable first: avoid a transient double award. These two native
            ; calls are not atomic; a brief empty interval is possible.
            OriginalChance.SetValue(100.0)
            ExpandedChance.SetValue(0.0)
        EndIf
        inserted = True
        Debug.Trace("XEP: activated enchanted branch " + VariantList)
        Stop()
    Else
        RegisterForSingleUpdate(10.0)
    EndIf
EndEvent

Bool Function IsUnlocked()
    If SourceQuests != None && SourceStages != None
        If SourceQuests.Length != SourceStages.Length
            Return False
        EndIf
        Int i = 0
        While i < SourceQuests.Length
            If SourceQuests[i] != None && SourceQuests[i].GetStageDone(SourceStages[i])
                Return True
            EndIf
            i += 1
        EndWhile
        Return False
    EndIf
    Return SourceQuest != None && SourceQuest.GetStageDone(SourceStage)
EndFunction
