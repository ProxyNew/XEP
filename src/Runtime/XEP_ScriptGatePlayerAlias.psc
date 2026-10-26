Scriptname XEP_ScriptGatePlayerAlias extends ReferenceAlias

Event OnPlayerLoadGame()
    XEP_ScriptGate controller = GetOwningQuest() as XEP_ScriptGate
    If controller
        controller.PlayerLoaded()
    EndIf
EndEvent
