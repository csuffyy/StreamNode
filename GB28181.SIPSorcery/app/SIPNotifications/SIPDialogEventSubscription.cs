﻿using System;
using System.Collections.Generic;
using System.Linq;
using SIPSorcery.SIP;

namespace GB28181.App
{
    public class SIPDialogEventSubscription : SIPEventSubscription
    {
        private const int MAX_DIALOGUES_FOR_NOTIFY = 25;

        private static string m_contentType = SIPMIMETypes.DIALOG_INFO_CONTENT_TYPE;

        private SIPEventDialogInfo DialogInfo;

        private SIPAssetGetListDelegate<SIPDialogueAsset> GetDialogues_External;
        private SIPAssetGetByIdDelegate<SIPDialogueAsset> GetDialogue_External;

        public override SIPEventPackage SubscriptionEventPackage
        {
            get { return SIPEventPackage.Dialog; }
        }

        public override string MonitorFilter
        {
            get { return "dialog " + ResourceURI.ToString(); }
        }

        public override string NotifyContentType
        {
            get { return m_contentType; }
        }

        public SIPDialogEventSubscription(
            string sessionID,
            SIPURI resourceURI,
            SIPURI canonincalResourceURI,
            string filter,
            SIPDialogue subscriptionDialogue,
            int expiry,
            SIPAssetGetListDelegate<SIPDialogueAsset> getDialogues,
            SIPAssetGetByIdDelegate<SIPDialogueAsset> getDialogue
        )
            : base(sessionID, resourceURI, canonincalResourceURI, filter, subscriptionDialogue, expiry)
        {
            GetDialogues_External = getDialogues;
            GetDialogue_External = getDialogue;
            DialogInfo = new SIPEventDialogInfo(0, SIPEventDialogInfoStateEnum.full, resourceURI);
        }

        public override void GetFullState()
        {
            try
            {
                DialogInfo.State = SIPEventDialogInfoStateEnum.full;
                List<SIPDialogueAsset> dialogueAssets =
                    GetDialogues_External(d => d.Owner == SubscriptionDialogue.Owner, "Inserted", 0,
                        MAX_DIALOGUES_FOR_NOTIFY);

                foreach (SIPDialogueAsset dialogueAsset in dialogueAssets)
                {
                    DialogInfo.DialogItems.Add(new SIPEventDialog(dialogueAsset.SIPDialogue.Id.ToString(), "confirmed",
                        dialogueAsset.SIPDialogue));
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPDialogEventSubscription GetFullState. ->" + excp.Message);
            }
        }

        public override string GetNotifyBody()
        {
            return DialogInfo.ToXMLText();
        }

        public override bool AddMonitorEvent(SIPMonitorMachineEvent machineEvent)
        {
            try
            {
                lock (DialogInfo)
                {
                    string state = GetStateForEventType(machineEvent.MachineEventType);

                    if (machineEvent.MachineEventType == SIPMonitorMachineEventTypesEnum.SIPDialogueRemoved)
                    {
                        DialogInfo.DialogItems.Add(new SIPEventDialog(machineEvent.ResourceID, state, null));
                        return true;
                    }
                    else
                    {
                        SIPDialogueAsset sipDialogue = GetDialogue_External(new Guid(machineEvent.ResourceID));

                        if (sipDialogue == null)
                        {
                            // Couldn't find the dialogue in the database so it must be terminated.
                            DialogInfo.DialogItems.Add(new SIPEventDialog(machineEvent.ResourceID, "terminated", null));
                            return true;
                        }
                        else if (machineEvent.MachineEventType == SIPMonitorMachineEventTypesEnum.SIPDialogueTransfer)
                        {
                            // For dialog transfer events add both dialogs involved to the notification.
                            DialogInfo.DialogItems.Add(new SIPEventDialog(sipDialogue.Id.ToString(), state,
                                sipDialogue.SIPDialogue));

                            if (sipDialogue.SIPDialogue.BridgeId != Guid.Empty)
                            {
                                SIPDialogueAsset bridgedDialogue =
                                    GetDialogues_External(
                                            d => d.BridgeId == sipDialogue.BridgeId && d.Id != sipDialogue.Id, null, 0,
                                            1)
                                        .FirstOrDefault();
                                if (bridgedDialogue != null)
                                {
                                    DialogInfo.DialogItems.Add(new SIPEventDialog(bridgedDialogue.Id.ToString(), state,
                                        bridgedDialogue.SIPDialogue));
                                }
                            }

                            return true;
                        }
                        else
                        {
                            DialogInfo.DialogItems.Add(new SIPEventDialog(sipDialogue.Id.ToString(), state,
                                sipDialogue.SIPDialogue));
                            return true;
                        }
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPDialogEventSubscription AddMonitorEvent. ->" + excp.Message);
                throw;
            }
        }

        public override void NotificationSent()
        {
            if (DialogInfo.State == SIPEventDialogInfoStateEnum.full)
            {
            }
            else
            {
                foreach (SIPEventDialog dialog in DialogInfo.DialogItems)
                {
                    string remoteURI = (dialog.RemoteParticipant != null && dialog.RemoteParticipant.URI != null)
                        ? ", " + dialog.RemoteParticipant.URI.ToString()
                        : null;
                }
            }

            DialogInfo.State = SIPEventDialogInfoStateEnum.partial;
            DialogInfo.DialogItems.RemoveAll(x => x.HasBeenSent);
            DialogInfo.Version++;
        }

        private string GetStateForEventType(SIPMonitorMachineEventTypesEnum machineEventType)
        {
            switch (machineEventType)
            {
                case SIPMonitorMachineEventTypesEnum.SIPDialogueCreated: return "confirmed";
                case SIPMonitorMachineEventTypesEnum.SIPDialogueRemoved: return "terminated";
                case SIPMonitorMachineEventTypesEnum.SIPDialogueUpdated: return "updated";
                case SIPMonitorMachineEventTypesEnum.SIPDialogueTransfer: return "updated";
                default:
                    throw new ApplicationException(
                        "The state for a dialog SIP event could not be determined from the monitor event type of " +
                        machineEventType + ".");
            }
        }
    }
}