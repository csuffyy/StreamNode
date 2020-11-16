//-----------------------------------------------------------------------------
// Filename: SIPClientUserAgent.cs
//
// Description: Implementation of a SIP Client User Agent that can be used to initiate SIP calls.
// 
// History:
// 22 Feb 2008	Aaron Clauson	    Created.
// 30 May 2020	Edward Chen         Updated.


using System;
using System.Collections.Generic;
using System.Net;
using GB28181.Net;
using GB28181.Sys;
using SIPSorcery.SIP;
using SIPSorcery.Sys;

namespace GB28181.App
{
    public class SIPClientUserAgent : ISIPClientUserAgent
    {
        private const int DNS_LOOKUP_TIMEOUT = 5000;

        private const char
            OUTBOUNDPROXY_AS_ROUTESET_CHAR =
                '<'; // If this character exists in the call descriptor OutboundProxy setting it gets treated as a Route set.

        //private static ILog logger = AppState.logger;
        // private static ILog rtccLogger = AppState.GetLogger("rtcc");

        private static string m_userAgent = SIPConstants.SIP_USERAGENT_STRING;
        private static readonly int m_defaultSIPPort = SIPConstants.DEFAULT_SIP_PORT;

        private static readonly string m_sdpContentType = SDP.SDP_MIME_CONTENTTYPE;
        //private static readonly int m_rtccInitialReservationSeconds = SIPSorcery.Entities.CustomerAccountDataLayer.INITIAL_RESERVATION_SECONDS;

        private SIPTransport m_sipTransport;
        private SIPEndPoint m_localSIPEndPoint;


        public string Owner { get; private set; } // If the UAC is authenticated holds the username of the client.

        public string
            AdminMemberId { get; private set; } // If the UAC is authenticated holds the username of the client.

        // Real-time call control properties.
        public string AccountCode { get; set; }
        public decimal ReservedCredit { get; set; }
        public int ReservedSeconds { get; set; }
        public decimal Rate { get; set; }

        private SIPCallDescriptor m_sipCallDescriptor; // Describes the server leg of the call from the sipswitch.
        private SIPEndPoint m_serverEndPoint;
        private UACInviteTransaction m_serverTransaction;

        private bool
            m_callCancelled; // It's possible for the call to be cancelled before the INVITE has been sent. This could occur if a DNS lookup on the server takes a while.

        private bool
            m_hungupOnCancel; // Set to true if a call has been cancelled AND and then an Ok response was received AND a BYE has been sent to hang it up. This variable is used to stop another BYE transaction being generated.

        private int m_serverAuthAttempts; // Used to determine if credentials for a server leg call fail.

        private SIPNonInviteTransaction
            m_cancelTransaction; // If the server call is cancelled this transaction contains the CANCEL in case it needs to be resent.

        private SIPEndPoint
            m_outboundProxy; // If the system needs to use an outbound proxy for every request this will be set and overrides any user supplied values.

        private SIPDialogue m_sipDialogue;

        //private SIPSorcery.Entities.CustomerAccountDataLayer m_customerAccountDataLayer = new SIPSorcery.Entities.CustomerAccountDataLayer();
        private RtccGetCustomerDelegate RtccGetCustomer_External;
        private RtccGetRateDelegate RtccGetRate_External;
        private RtccGetBalanceDelegate RtccGetBalance_External;
        private RtccReserveInitialCreditDelegate RtccReserveInitialCredit_External;
        private RtccUpdateCdrDelegate RtccUpdateCdr_External;

        public event SIPCallResponseDelegate CallTrying;
        public event SIPCallResponseDelegate CallRinging;
        public event SIPCallResponseDelegate CallAnswered;
        public event SIPCallFailedDelegate CallFailed;

        public UACInviteTransaction ServerTransaction
        {
            get { return m_serverTransaction; }
        }

        public bool IsUACAnswered
        {
            get { return m_serverTransaction.TransactionFinalResponse != null; }
        }

        public SIPDialogue SIPDialogue
        {
            get { return m_sipDialogue; }
        }

        public SIPCallDescriptor CallDescriptor
        {
            get { return m_sipCallDescriptor; }
        }

        public SIPClientUserAgent(
            SIPTransport sipTransport,
            SIPEndPoint outboundProxy,
            string owner,
            string adminMemberId
        )
        {
            m_sipTransport = sipTransport;
            m_outboundProxy = (outboundProxy != null) ? SIPEndPoint.ParseSIPEndPoint(outboundProxy.ToString()) : null;
            Owner = owner;
            AdminMemberId = adminMemberId;
        }

        public SIPClientUserAgent(
            SIPTransport sipTransport,
            SIPEndPoint outboundProxy,
            string owner,
            string adminMemberId,
            RtccGetCustomerDelegate rtccGetCustomer,
            RtccGetRateDelegate rtccGetRate,
            RtccGetBalanceDelegate rtccGetBalance,
            RtccReserveInitialCreditDelegate rtccReserveInitialCredit,
            RtccUpdateCdrDelegate rtccUpdateCdr
        ) : this(sipTransport, outboundProxy, owner, adminMemberId)
        {
            RtccGetCustomer_External = rtccGetCustomer;
            RtccGetRate_External = rtccGetRate;
            RtccGetBalance_External = rtccGetBalance;
            RtccReserveInitialCredit_External = rtccReserveInitialCredit;
            RtccUpdateCdr_External = rtccUpdateCdr;
        }


        public void Call(SIPCallDescriptor sipCallDescriptor)
        {
            try
            {
                m_sipCallDescriptor = sipCallDescriptor;
                SIPURI callURI = SIPURI.ParseSIPURI(sipCallDescriptor.Uri);
                SIPRouteSet routeSet = null;

                if (!m_callCancelled)
                {
                    // If the outbound proxy is a loopback address, as it will normally be for local deployments, then it cannot be overriden.
                    if (m_outboundProxy != null && IPAddress.IsLoopback(m_outboundProxy.Address))
                    {
                        m_serverEndPoint = m_outboundProxy;
                    }
                    else if (!sipCallDescriptor.ProxySendFrom.IsNullOrBlank())
                    {
                        // If the binding has a specific proxy end point sent then the request needs to be forwarded to the proxy's default end point for it to take care of.
                        SIPEndPoint outboundProxyEndPoint =
                            SIPEndPoint.ParseSIPEndPoint(sipCallDescriptor.ProxySendFrom);
                        m_outboundProxy = new SIPEndPoint(SIPProtocolsEnum.udp,
                            new IPEndPoint(outboundProxyEndPoint.Address, m_defaultSIPPort));
                        m_serverEndPoint = m_outboundProxy;
                    }
                    else if (m_outboundProxy != null)
                    {
                        // Using the system outbound proxy only, no additional user routing requirements.
                        m_serverEndPoint = m_outboundProxy;
                    }

                    // A custom route set may have been specified for the call.
                    if (m_sipCallDescriptor.RouteSet != null &&
                        m_sipCallDescriptor.RouteSet.IndexOf(OUTBOUNDPROXY_AS_ROUTESET_CHAR) != -1)
                    {
                        try
                        {
                            routeSet = new SIPRouteSet();
                            routeSet.PushRoute(new SIPRoute(m_sipCallDescriptor.RouteSet, true));
                        }
                        catch
                        {
                        }
                    }

                    // No outbound proxy, determine the forward destination based on the SIP request.
                    if (m_serverEndPoint == null)
                    {
                        SIPDNSLookupResult lookupResult = null;

                        if (routeSet == null || routeSet.Length == 0)
                        {
                            lookupResult = m_sipTransport.GetURIEndPoint(callURI, false);
                        }
                        else
                        {
                            lookupResult = m_sipTransport.GetURIEndPoint(routeSet.TopRoute.URI, false);
                        }

                        if (lookupResult.LookupError != null)
                        {
                        }
                        else
                        {
                            m_serverEndPoint = lookupResult.GetSIPEndPoint();
                        }
                    }

                    if (m_callCancelled)
                    {
                        FireCallFailed(this, "Cancelled by caller");
                    }
                    else if (m_serverEndPoint != null)
                    {
                        m_localSIPEndPoint = m_sipTransport.GetDefaultSIPEndPoint(m_serverEndPoint);
                        if (m_localSIPEndPoint == null)
                        {
                            throw new ApplicationException(
                                "The call could not locate an appropriate SIP transport channel for protocol " +
                                callURI.Protocol + ".");
                        }

                        string content = sipCallDescriptor.Content;

                        if (content.IsNullOrBlank())
                        {
                        }
                        else if (m_sipCallDescriptor.ContentType == m_sdpContentType)
                        {
                            if (!m_sipCallDescriptor.MangleResponseSDP)
                            {
                                IPEndPoint sdpEndPoint = SDP.GetSDPRTPEndPoint(content);
                                if (sdpEndPoint != null)
                                {
                                }
                                else
                                {
                                }
                            }
                            else
                            {
                                IPEndPoint sdpEndPoint = SDP.GetSDPRTPEndPoint(content);
                                if (sdpEndPoint != null)
                                {
                                    if (!IPSocket.IsPrivateAddress(sdpEndPoint.Address.ToString()))
                                    {
                                    }
                                    else
                                    {
                                        bool wasSDPMangled = false;
                                        if (sipCallDescriptor.MangleIPAddress != null)
                                        {
                                            if (sdpEndPoint != null)
                                            {
                                                content = SIPPacketMangler.MangleSDP(content,
                                                    sipCallDescriptor.MangleIPAddress.ToString(), out wasSDPMangled);
                                            }
                                        }

                                        if (wasSDPMangled)
                                        {
                                        }
                                        else if (sdpEndPoint != null)
                                        {
                                        }
                                    }
                                }
                                else
                                {
                                }
                            }
                        }

                        SIPRequest switchServerInvite = GetInviteRequest(m_sipCallDescriptor,
                            CallProperties.CreateBranchId(), CallProperties.CreateNewCallId(), m_localSIPEndPoint,
                            routeSet, content, sipCallDescriptor.ContentType);

                        // Now that we have a destination socket create a new UAC transaction for forwarded leg of the call.
                        m_serverTransaction = m_sipTransport.CreateUACTransaction(switchServerInvite, m_serverEndPoint,
                            m_localSIPEndPoint, m_outboundProxy);
                        m_serverTransaction.CDR.DialPlanContextID = m_sipCallDescriptor.DialPlanContextID;

                        #region Real-time call control processing.

                        string rtccError = null;

                        if (m_serverTransaction.CDR != null)
                        {
                            m_serverTransaction.CDR.Owner = Owner;
                            m_serverTransaction.CDR.AdminMemberId = AdminMemberId;

                            m_serverTransaction.CDR.Updated();


                            if (m_sipCallDescriptor.AccountCode != null && RtccGetCustomer_External != null)
                            {
                                //var customerAccount = m_customerAccountDataLayer.CheckAccountCode(Owner, m_sipCallDescriptor.AccountCode);
                                var customerAccount = RtccGetCustomer_External(Owner, m_sipCallDescriptor.AccountCode);

                                if (customerAccount == null)
                                {
                                    Logger.Logger.Debug(
                                        "A billable call could not proceed as no account exists for account code or number " +
                                        m_sipCallDescriptor.AccountCode + " and owner " + Owner + ".");
                                    rtccError = "Real-time call control invalid account code";
                                }
                                else
                                {
                                    AccountCode = customerAccount.AccountCode;

                                    string rateDestination = m_sipCallDescriptor.Uri;

                                    if (SIPURI.TryParse(m_sipCallDescriptor.Uri, out SIPURI uri))
                                    {
                                        rateDestination = SIPURI.ParseSIPURIRelaxed(m_sipCallDescriptor.Uri).User;
                                    }

                                    //var rate = m_customerAccountDataLayer.GetRate(Owner, m_sipCallDescriptor.RateCode, rateDestination, customerAccount.RatePlan);
                                    var rate = RtccGetRate_External(Owner, m_sipCallDescriptor.RateCode,
                                        rateDestination, customerAccount.RatePlan);

                                    if (rate == null)
                                    {
                                        Logger.Logger.Debug(
                                            "A billable call could not proceed as no rate could be determined for destination " +
                                            rateDestination + " and owner " + Owner + ".");
                                        rtccError = "Real-time call control no rate";
                                    }
                                    else
                                    {
                                        Rate = rate.RatePerIncrement;

                                        if (rate.RatePerIncrement == 0 && rate.SetupCost == 0)
                                        {
                                        }
                                        else
                                        {
                                            //decimal balance = m_customerAccountDataLayer.GetBalance(AccountCode);
                                            decimal balance = RtccGetBalance_External(AccountCode);

                                            if (balance < Rate)
                                            {
                                                Logger.Logger.Debug(
                                                    "A billable call could not proceed as the available credit for " +
                                                    AccountCode + " was not sufficient for 60 seconds to destination " +
                                                    rateDestination + " and owner " + Owner + ".");
                                                rtccError = "Real-time call control insufficient credit";
                                            }
                                            else
                                            {
                                                int intialSeconds = 0;
                                                //var reservationCost = m_customerAccountDataLayer.ReserveInitialCredit(AccountCode, rate, m_serverTransaction.CDR, out intialSeconds);
                                                var reservationCost = RtccReserveInitialCredit_External(AccountCode,
                                                    rate.ID, m_serverTransaction.CDR, out intialSeconds);

                                                if (reservationCost == Decimal.MinusOne)
                                                {
                                                    Logger.Logger.Debug(
                                                        "Call will not proceed as the intial real-time call control credit reservation failed for owner " +
                                                        Owner + ".");
                                                    rtccError = "Real-time call control initial reservation failed";
                                                }
                                                else
                                                {
                                                    ReservedCredit = reservationCost;
                                                    ReservedSeconds = intialSeconds;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // If this is a billable call attempt to reserve the first chunk of credit.
                        //if (m_serverTransaction.CDR != null && AccountCode.NotNullOrBlank())
                        //{
                        //    m_serverTransaction.CDR.AccountCode = AccountCode;
                        //    m_serverTransaction.CDR.Rate = Rate;
                        //    //m_serverTransaction.CDR.Cost = ReservedCredit;
                        //    //m_serverTransaction.CDR.SecondsReserved = ReservedSeconds;

                        //    var reservationCost = m_customerAccountDataLayer.ReserveInitialCredit(AccountCode, Rate, m_rtccInitialReservationSeconds);

                        //    if (reservationCost == Decimal.MinusOne)
                        //    {
                        //        Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "Call will not proceed as the intial real-time call control credit reservation failed.", Owner));
                        //    }
                        //    else
                        //    {
                        //        m_serverTransaction.CDR.SecondsReserved = m_rtccInitialReservationSeconds;
                        //        m_serverTransaction.CDR.Cost = reservationCost;
                        //    }
                        //}

                        #endregion

                        if (rtccError == null)
                        {
                            m_serverTransaction.UACInviteTransactionInformationResponseReceived +=
                                ServerInformationResponseReceived;
                            m_serverTransaction.UACInviteTransactionFinalResponseReceived +=
                                ServerFinalResponseReceived;
                            m_serverTransaction.UACInviteTransactionTimedOut += ServerTimedOut;
                            m_serverTransaction.TransactionTraceMessage += TransactionTraceMessage;

                            m_serverTransaction.SendInviteRequest(m_serverEndPoint,
                                m_serverTransaction.TransactionRequest);
                        }
                        else
                        {
                            m_serverTransaction.CancelCall(rtccError);
                            FireCallFailed(this, rtccError);
                        }
                    }
                    else
                    {
                        if (routeSet == null || routeSet.Length == 0)
                        {
                            m_serverTransaction.CancelCall("Unresolvable destination " + callURI.Host);
                            FireCallFailed(this, "unresolvable destination " + callURI.Host);
                        }
                        else
                        {
                            m_serverTransaction.CancelCall("Unresolvable destination " + routeSet.TopRoute.Host);
                            FireCallFailed(this, "unresolvable destination " + routeSet.TopRoute.Host);
                        }
                    }
                }
            }
            catch (ApplicationException appExcp)
            {
                if (m_serverTransaction != null)
                {
                    m_serverTransaction.CancelCall(appExcp.Message);
                }

                FireCallFailed(this, appExcp.Message);
            }
            catch (Exception excp)
            {
                if (m_serverTransaction != null)
                {
                    m_serverTransaction.CancelCall("Unknown exception");
                }

                FireCallFailed(this, excp.Message);
            }
        }

        public void Cancel()
        {
            try
            {
                m_callCancelled = true;

                // Cancel server call.
                if (m_serverTransaction == null)
                {
                }
                else if (m_cancelTransaction != null)
                {
                    if (m_cancelTransaction.TransactionState != SIPTransactionStatesEnum.Completed)
                    {
                        m_cancelTransaction.SendRequest(m_cancelTransaction.TransactionRequest);
                    }
                    else
                    {
                    }
                }
                else //if (m_serverTransaction.TransactionState == SIPTransactionStatesEnum.Proceeding || m_serverTransaction.TransactionState == SIPTransactionStatesEnum.Trying)
                {
                    //logger.Debug("Cancelling forwarded call leg, sending CANCEL to " + ForwardedTransaction.TransactionRequest.URI.ToString() + " (transid: " + ForwardedTransaction.TransactionId + ").");

                    // No reponse has been received from the server so no CANCEL request neccessary, stop any retransmits of the INVITE.
                    m_serverTransaction.CancelCall();

                    SIPRequest cancelRequest = GetCancelRequest(m_serverTransaction.TransactionRequest);
                    m_cancelTransaction = m_sipTransport.CreateNonInviteTransaction(cancelRequest, m_serverEndPoint,
                        m_serverTransaction.LocalSIPEndPoint, m_outboundProxy);
                    m_cancelTransaction.TransactionTraceMessage += TransactionTraceMessage;
                    //m_cancelTransaction.SendRequest(m_serverEndPoint, cancelRequest);
                    m_cancelTransaction.SendReliableRequest();
                }
                //else
                //{
                // No reponse has been received from the server so no CANCEL request neccessary, stop any retransmits of the INVITE.
                //    m_serverTransaction.CancelCall();
                //    Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.UserAgentClient, SIPMonitorEventTypesEnum.DialPlan, "Cancelling forwarded call leg " + m_sipCallDescriptor.Uri.ToString() + ", no response from server has been received so no CANCEL request required.", Owner));
                //}

                FireCallFailed(this, "Call cancelled by user.");
            }
            catch (Exception excp)
            {
            }
        }

        public void Update(CRMHeaders crmHeaders)
        {
            SIPRequest updateRequest = GetUpdateRequest(m_serverTransaction.TransactionRequest, crmHeaders);
            SIPNonInviteTransaction updateTransaction = m_sipTransport.CreateNonInviteTransaction(updateRequest,
                m_serverEndPoint, m_serverTransaction.LocalSIPEndPoint, m_outboundProxy);
            updateTransaction.TransactionTraceMessage += TransactionTraceMessage;
            updateTransaction.SendReliableRequest();
        }

        private void ServerFinalResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint,
            SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            try
            {
                //if (Thread.CurrentThread.Name.IsNullOrBlank())
                //{
                //    Thread.CurrentThread.Name = THREAD_NAME + DateTime.Now.ToString("HHmmss") + "-" + Crypto.GetRandomString(3);
                //}

                //m_sipTrace += "Received " + DateTime.Now.ToString("dd MMM yyyy HH:mm:ss") + " " + localEndPoint + "<-" + remoteEndPoint + "\r\n" + sipResponse.ToString();

                m_serverTransaction.UACInviteTransactionInformationResponseReceived -=
                    ServerInformationResponseReceived;
                m_serverTransaction.UACInviteTransactionFinalResponseReceived -= ServerFinalResponseReceived;
                m_serverTransaction.TransactionTraceMessage -= TransactionTraceMessage;

                if (m_callCancelled && sipResponse.Status == SIPResponseStatusCodesEnum.RequestTerminated)
                {
                    // No action required. Correctly received request terminated on an INVITE we cancelled.
                }
                else if (m_callCancelled)
                {
                    #region Call has been cancelled, hangup.

                    if (m_hungupOnCancel)
                    {
                    }
                    else
                    {
                        m_hungupOnCancel = true;


                        if (sipResponse.Header.Contact != null && sipResponse.Header.Contact.Count > 0)
                        {
                            SIPURI byeURI = sipResponse.Header.Contact[0].ContactURI;
                            SIPRequest byeRequest = GetByeRequest(sipResponse, byeURI, localSIPEndPoint);

                            //SIPEndPoint byeEndPoint = m_sipTransport.GetRequestEndPoint(byeRequest, m_outboundProxy, true);

                            // if (byeEndPoint != null)
                            // {
                            SIPNonInviteTransaction byeTransaction =
                                m_sipTransport.CreateNonInviteTransaction(byeRequest, null, localSIPEndPoint,
                                    m_outboundProxy);
                            byeTransaction.SendReliableRequest();
                            // }
                            // else
                            // {
                            //     Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.UserAgentClient, SIPMonitorEventTypesEnum.DialPlan, "Could not end BYE on cancelled call as request end point could not be determined " + byeRequest.URI.ToString(), Owner));
                            //}
                        }
                        else
                        {
                        }
                    }

                    #endregion
                }
                else if (sipResponse.Status == SIPResponseStatusCodesEnum.ProxyAuthenticationRequired ||
                         sipResponse.Status == SIPResponseStatusCodesEnum.Unauthorised)
                {
                    //logger.Debug("AuthReqd Final response " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + " for " + m_serverTransaction.TransactionRequest.URI.ToString() + ".");

                    #region Authenticate client call to third party server.

                    if (!m_callCancelled)
                    {
                        if (m_sipCallDescriptor.Password.IsNullOrBlank())
                        {
                            // No point trying to authenticate if there is no password to use.
                            FireCallFailed(this, "Authentication requested when no credentials available");
                        }
                        else if (m_serverAuthAttempts == 0)
                        {
                            m_serverAuthAttempts = 1;

                            // Resend INVITE with credentials.
                            string username =
                                (m_sipCallDescriptor.AuthUsername != null &&
                                 m_sipCallDescriptor.AuthUsername.Trim().Length > 0)
                                    ? m_sipCallDescriptor.AuthUsername
                                    : m_sipCallDescriptor.Username;
                            SIPAuthorisationDigest authRequest = sipResponse.Header.AuthenticationHeader.SIPDigest;
                            authRequest.SetCredentials(username, m_sipCallDescriptor.Password, m_sipCallDescriptor.Uri,
                                SIPMethodsEnum.INVITE.ToString());

                            SIPRequest authInviteRequest = m_serverTransaction.TransactionRequest;

                            //if (SIPProviderMagicJack.IsMagicJackRequest(sipResponse))
                            //{
                            //    authInviteRequest.Header.AuthenticationHeader = SIPProviderMagicJack.GetAuthenticationHeader(sipResponse);
                            //}
                            //else
                            //{
                            authInviteRequest.Header.AuthenticationHeader = new SIPAuthenticationHeader(authRequest);
                            authInviteRequest.Header.AuthenticationHeader.SIPDigest.Response = authRequest.Digest;
                            //}

                            authInviteRequest.Header.Vias.TopViaHeader.Branch = CallProperties.CreateBranchId();
                            authInviteRequest.Header.CSeq = authInviteRequest.Header.CSeq + 1;

                            // Create a new UAC transaction to establish the authenticated server call.
                            var originalCallTransaction = m_serverTransaction;
                            m_serverTransaction = m_sipTransport.CreateUACTransaction(authInviteRequest,
                                m_serverEndPoint, localSIPEndPoint, m_outboundProxy);
                            if (m_serverTransaction.CDR != null)
                            {
                                m_serverTransaction.CDR.Owner = Owner;
                                m_serverTransaction.CDR.AdminMemberId = AdminMemberId;
                                m_serverTransaction.CDR.DialPlanContextID = m_sipCallDescriptor.DialPlanContextID;

                                m_serverTransaction.CDR.Updated();

                                if (AccountCode != null)
                                {
                                    //var rtccCDR = new SIPSorcery.Entities.CDR()
                                    //{
                                    //    ID = m_serverTransaction.CDR.CDRId.ToString(),
                                    //    Owner = m_serverTransaction.CDR.Owner,
                                    //    AdminMemberID = m_serverTransaction.CDR.AdminMemberId,
                                    //    Inserted = DateTimeOffset.UtcNow.ToString("o"),
                                    //    Created = DateTimeOffset.UtcNow.ToString("o"),
                                    //    DstHost = "",
                                    //    DstURI = m_sipCallDescriptor.Uri,
                                    //    CallID = "",
                                    //    FromHeader = m_sipCallDescriptor.From,
                                    //    LocalSocket = "udp:0.0.0.0:5060",
                                    //    RemoteSocket = "udp:0.0.0.0:5060",
                                    //    Direction = m_serverTransaction.CDR.CallDirection.ToString(),
                                    //    DialPlanContextID = m_sipCallDescriptor.DialPlanContextID.ToString()
                                    //};
#if !SILVERLIGHT
                                    //m_customerAccountDataLayer.UpdateRealTimeCallControlCDRID(originalCallTransaction.CDR.CDRId.ToString(), m_serverTransaction.CDR);
                                    RtccUpdateCdr_External(originalCallTransaction.CDR.CDRId.ToString(),
                                        m_serverTransaction.CDR);
#endif

                                    //m_serverTransaction.CDR.AccountCode = AccountCode;
                                    //m_serverTransaction.CDR.Rate = Rate;

                                    // Transfer any credit reservations from the original call to the new call.
                                    //m_serverTransaction.CDR.SecondsReserved = originalCallTransaction.CDR.SecondsReserved;
                                    //m_serverTransaction.CDR.Cost = originalCallTransaction.CDR.Cost;
                                    //m_serverTransaction.CDR.IncrementSeconds = originalCallTransaction.CDR.IncrementSeconds;
                                    //originalCallTransaction.CDR.SecondsReserved = 0;
                                    //originalCallTransaction.CDR.Cost = 0;
                                    //originalCallTransaction.CDR.ReconciliationResult = "reallocated";
                                    //originalCallTransaction.CDR.IsHangingUp = true;
                                }

                                Logger.Logger.Debug("RTCC reservation was reallocated from CDR " +
                                                    originalCallTransaction.CDR.CDRId + " to " +
                                                    m_serverTransaction.CDR.CDRId + " for owner " + Owner + ".");
                            }

                            m_serverTransaction.UACInviteTransactionInformationResponseReceived +=
                                ServerInformationResponseReceived;
                            m_serverTransaction.UACInviteTransactionFinalResponseReceived +=
                                ServerFinalResponseReceived;
                            m_serverTransaction.UACInviteTransactionTimedOut += ServerTimedOut;
                            m_serverTransaction.TransactionTraceMessage += TransactionTraceMessage;

                            //logger.Debug("Sending authenticated switchcall INVITE to " + ForwardedCallStruct.Host + ".");
                            m_serverTransaction.SendInviteRequest(m_serverEndPoint, authInviteRequest);
                            //m_sipTrace += "Sending " + DateTime.Now.ToString("dd MMM yyyy HH:mm:ss") + " " + localEndPoint + "->" + ForwardedTransaction.TransactionRequest.GetRequestEndPoint() + "\r\n" + ForwardedTransaction.TransactionRequest.ToString();
                        }
                        else
                        {
                            //logger.Debug("Authentication of client call to switch server failed.");
                            FireCallFailed(this, "Authentication with provided credentials failed");
                        }
                    }

                    #endregion
                }
                else
                {
                    if (sipResponse.StatusCode >= 200 && sipResponse.StatusCode <= 299)
                    {
                        if (sipResponse.Body.IsNullOrBlank())
                        {
                        }
                        else if (m_sipCallDescriptor.ContentType == m_sdpContentType)
                        {
                            if (!m_sipCallDescriptor.MangleResponseSDP)
                            {
                                IPEndPoint sdpEndPoint = SDP.GetSDPRTPEndPoint(sipResponse.Body);
                                string sdpSocket = (sdpEndPoint != null)
                                    ? sdpEndPoint.ToString()
                                    : "could not determine";
                            }
                            else
                            {
                                //m_callInProgress = false; // the call is now established
                                //logger.Debug("Final response " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + " for " + ForwardedTransaction.TransactionRequest.URI.ToString() + ".");
                                // Determine of response SDP should be mangled.

                                IPEndPoint sdpEndPoint = SDP.GetSDPRTPEndPoint(sipResponse.Body);
                                //Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.AppServer, SIPMonitorEventTypesEnum.DialPlan, "UAC response SDP was mangled from sdp=" + sdpEndPoint.Address.ToString() + ", proxyfrom=" + sipResponse.Header.ProxyReceivedFrom + ", mangle=" + m_sipCallDescriptor.MangleResponseSDP + ".", null));
                                if (sdpEndPoint != null)
                                {
                                    if (!IPSocket.IsPrivateAddress(sdpEndPoint.Address.ToString()))
                                    {
                                    }
                                    else
                                    {
                                        bool wasSDPMangled = false;
                                        string publicIPAddress = null;

                                        if (!sipResponse.Header.ProxyReceivedFrom.IsNullOrBlank())
                                        {
                                            IPAddress remoteUASAddress = SIPEndPoint
                                                .ParseSIPEndPoint(sipResponse.Header.ProxyReceivedFrom).Address;
                                            if (IPSocket.IsPrivateAddress(remoteUASAddress.ToString()) &&
                                                m_sipCallDescriptor.MangleIPAddress != null)
                                            {
                                                // If the response has arrived here on a private IP address then it must be
                                                // for a local version install and an incoming call that needs it's response mangled.
                                                if (!IPSocket.IsPrivateAddress(m_sipCallDescriptor.MangleIPAddress
                                                    .ToString()))
                                                {
                                                    publicIPAddress = m_sipCallDescriptor.MangleIPAddress.ToString();
                                                }
                                            }
                                            else
                                            {
                                                publicIPAddress = remoteUASAddress.ToString();
                                            }
                                        }
                                        else if (!IPSocket.IsPrivateAddress(remoteEndPoint.Address.ToString()) &&
                                                 remoteEndPoint.Address != IPAddress.Any)
                                        {
                                            publicIPAddress = remoteEndPoint.Address.ToString();
                                        }
                                        else if (m_sipCallDescriptor.MangleIPAddress != null)
                                        {
                                            publicIPAddress = m_sipCallDescriptor.MangleIPAddress.ToString();
                                        }

                                        if (publicIPAddress != null)
                                        {
                                            sipResponse.Body = SIPPacketMangler.MangleSDP(sipResponse.Body,
                                                publicIPAddress, out wasSDPMangled);
                                        }

                                        if (wasSDPMangled)
                                        {
                                        }
                                        else if (sdpEndPoint != null)
                                        {
                                        }
                                    }
                                }
                                else
                                {
                                }
                            }
                        }

                        m_sipDialogue = new SIPDialogue(m_serverTransaction, Owner, AdminMemberId);
                        m_sipDialogue.CallDurationLimit = m_sipCallDescriptor.CallDurationLimit;

                        // Set switchboard dialogue values from the answered response or from dialplan set values.
                        //m_sipDialogue.SwitchboardCallerDescription = sipResponse.Header.SwitchboardCallerDescription;
                        m_sipDialogue.SwitchboardLineName = sipResponse.Header.SwitchboardLineName;
                        m_sipDialogue.CRMPersonName = sipResponse.Header.CRMPersonName;
                        m_sipDialogue.CRMCompanyName = sipResponse.Header.CRMCompanyName;
                        m_sipDialogue.CRMPictureURL = sipResponse.Header.CRMPictureURL;

                        if (m_sipCallDescriptor.SwitchboardHeaders != null)
                        {
                            //if (!m_sipCallDescriptor.SwitchboardHeaders.SwitchboardDialogueDescription.IsNullOrBlank())
                            //{
                            //    m_sipDialogue.SwitchboardDescription = m_sipCallDescriptor.SwitchboardHeaders.SwitchboardDialogueDescription;
                            //}

                            m_sipDialogue.SwitchboardLineName =
                                m_sipCallDescriptor.SwitchboardHeaders.SwitchboardLineName;
                            m_sipDialogue.SwitchboardOwner = m_sipCallDescriptor.SwitchboardHeaders.SwitchboardOwner;
                        }
                    }

                    FireCallAnswered(this, sipResponse);
                }
            }
            catch (Exception excp)
            {
            }
        }

        private void ServerInformationResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint,
            SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            if (m_callCancelled)
            {
                // Call was cancelled in the interim.
                Cancel();
            }
            else
            {
                if (sipResponse.Status == SIPResponseStatusCodesEnum.Ringing ||
                    sipResponse.Status == SIPResponseStatusCodesEnum.SessionProgress)
                {
                    FireCallRinging(this, sipResponse);
                }
                else
                {
                    FireCallTrying(this, sipResponse);
                }
            }
        }

        private void ServerTimedOut(SIPTransaction sipTransaction)
        {
            if (!m_callCancelled)
            {
                FireCallFailed(this, "Timeout, no response from server");
            }
        }

        //private void ByeFinalResponseReceived(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, SIPTransaction sipTransaction, SIPResponse sipResponse)
        //{
        //    Log_External(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.UserAgentClient, SIPMonitorEventTypesEnum.DialPlan, "BYE response " + sipResponse.StatusCode + " " + sipResponse.ReasonPhrase + ".", Owner));
        //}

        private SIPRequest GetInviteRequest(SIPCallDescriptor sipCallDescriptor, string branchId, string callId,
            SIPEndPoint localSIPEndPoint, SIPRouteSet routeSet, string content, string contentType)
        {
            SIPRequest inviteRequest = new SIPRequest(SIPMethodsEnum.INVITE, sipCallDescriptor.Uri);
            inviteRequest.LocalSIPEndPoint = localSIPEndPoint;

            SIPHeader inviteHeader = new SIPHeader(sipCallDescriptor.GetFromHeader(),
                SIPToHeader.ParseToHeader(sipCallDescriptor.To), 1, callId);

            inviteHeader.From.FromTag = CallProperties.CreateNewTag();

            // For incoming calls forwarded via the dial plan the username needs to go into the Contact header.
            inviteHeader.Contact = new List<SIPContactHeader>()
                {new SIPContactHeader(null, new SIPURI(inviteRequest.URI.Scheme, localSIPEndPoint))};
            inviteHeader.Contact[0].ContactURI.User = sipCallDescriptor.Username;
            inviteHeader.CSeqMethod = SIPMethodsEnum.INVITE;
            inviteHeader.UserAgent = m_userAgent;
            inviteHeader.Routes = routeSet;
            inviteRequest.Header = inviteHeader;

            if (!sipCallDescriptor.ProxySendFrom.IsNullOrBlank())
            {
                inviteHeader.ProxySendFrom = sipCallDescriptor.ProxySendFrom;
            }

            SIPViaHeader viaHeader = new SIPViaHeader(localSIPEndPoint, branchId);
            inviteRequest.Header.Vias.PushViaHeader(viaHeader);

            inviteRequest.Body = content;
            inviteRequest.Header.ContentLength = (inviteRequest.Body != null) ? inviteRequest.Body.Length : 0;
            inviteRequest.Header.ContentType = contentType;

            // Add custom switchboard headers.
            if (CallDescriptor.SwitchboardHeaders != null)
            {
                inviteHeader.SwitchboardOriginalCallID = CallDescriptor.SwitchboardHeaders.SwitchboardOriginalCallID;
                //inviteHeader.SwitchboardCallerDescription = CallDescriptor.SwitchboardHeaders.SwitchboardCallerDescription;
                inviteHeader.SwitchboardLineName = CallDescriptor.SwitchboardHeaders.SwitchboardLineName;
                //inviteHeader.SwitchboardOwner = CallDescriptor.SwitchboardHeaders.SwitchboardOwner;
                //inviteHeader.SwitchboardOriginalFrom = CallDescriptor.SwitchboardHeaders.SwitchboardOriginalFrom;
            }

            // Add custom CRM headers.
            if (CallDescriptor.CRMHeaders != null)
            {
                inviteHeader.CRMPersonName = CallDescriptor.CRMHeaders.PersonName;
                inviteHeader.CRMCompanyName = CallDescriptor.CRMHeaders.CompanyName;
                inviteHeader.CRMPictureURL = CallDescriptor.CRMHeaders.AvatarURL;
            }

            try
            {
                if (sipCallDescriptor.CustomHeaders != null && sipCallDescriptor.CustomHeaders.Count > 0)
                {
                    foreach (string customHeader in sipCallDescriptor.CustomHeaders)
                    {
                        if (customHeader.IsNullOrBlank())
                        {
                            continue;
                        }
                        else if (customHeader.Trim().StartsWith(SIPHeaders.SIP_HEADER_USERAGENT))
                        {
                            inviteRequest.Header.UserAgent =
                                customHeader.Substring(customHeader.IndexOf(":") + 1).Trim();
                        }
                        else
                        {
                            inviteRequest.Header.UnknownHeaders.Add(customHeader);
                        }
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception Parsing CustomHeader for GetInviteRequest. ->" + excp.Message +
                                    sipCallDescriptor.CustomHeaders);
            }

            return inviteRequest;
        }

        private SIPRequest GetCancelRequest(SIPRequest inviteRequest)
        {
            SIPRequest cancelRequest = new SIPRequest(SIPMethodsEnum.CANCEL, inviteRequest.URI);
            cancelRequest.LocalSIPEndPoint = inviteRequest.LocalSIPEndPoint;

            SIPHeader inviteHeader = inviteRequest.Header;
            SIPHeader cancelHeader =
                new SIPHeader(inviteHeader.From, inviteHeader.To, inviteHeader.CSeq, inviteHeader.CallId);
            cancelRequest.Header = cancelHeader;
            cancelHeader.CSeqMethod = SIPMethodsEnum.CANCEL;
            cancelHeader.Routes = inviteHeader.Routes;
            cancelHeader.ProxySendFrom = inviteHeader.ProxySendFrom;
            cancelHeader.Vias = inviteHeader.Vias;

            return cancelRequest;
        }

        private SIPRequest GetByeRequest(SIPResponse inviteResponse, SIPURI byeURI, SIPEndPoint localSIPEndPoint)
        {
            SIPRequest byeRequest = new SIPRequest(SIPMethodsEnum.BYE, byeURI);
            byeRequest.LocalSIPEndPoint = localSIPEndPoint;

            SIPFromHeader byeFromHeader = inviteResponse.Header.From;
            SIPToHeader byeToHeader = inviteResponse.Header.To;
            int cseq = inviteResponse.Header.CSeq + 1;

            SIPHeader byeHeader = new SIPHeader(byeFromHeader, byeToHeader, cseq, inviteResponse.Header.CallId);
            byeHeader.CSeqMethod = SIPMethodsEnum.BYE;
            byeHeader.ProxySendFrom = m_serverTransaction.TransactionRequest.Header.ProxySendFrom;
            byeRequest.Header = byeHeader;

            byeRequest.Header.Routes = (inviteResponse.Header.RecordRoutes != null)
                ? inviteResponse.Header.RecordRoutes.Reversed()
                : null;

            SIPViaHeader viaHeader = new SIPViaHeader(localSIPEndPoint, CallProperties.CreateBranchId());
            byeRequest.Header.Vias.PushViaHeader(viaHeader);

            return byeRequest;
        }

        private SIPRequest GetUpdateRequest(SIPRequest inviteRequest, CRMHeaders crmHeaders)
        {
            SIPRequest updateRequest = new SIPRequest(SIPMethodsEnum.UPDATE, inviteRequest.URI);
            updateRequest.LocalSIPEndPoint = inviteRequest.LocalSIPEndPoint;

            SIPHeader inviteHeader = inviteRequest.Header;
            SIPHeader updateHeader = new SIPHeader(inviteHeader.From, inviteHeader.To, inviteHeader.CSeq + 1,
                inviteHeader.CallId);
            inviteRequest.Header.CSeq++;
            updateRequest.Header = updateHeader;
            updateHeader.CSeqMethod = SIPMethodsEnum.UPDATE;
            updateHeader.Routes = inviteHeader.Routes;
            updateHeader.ProxySendFrom = inviteHeader.ProxySendFrom;

            SIPViaHeader viaHeader = new SIPViaHeader(inviteRequest.LocalSIPEndPoint, CallProperties.CreateBranchId());
            updateHeader.Vias.PushViaHeader(viaHeader);

            // Add custom CRM headers.
            if (crmHeaders != null)
            {
                updateHeader.CRMPersonName = crmHeaders.PersonName;
                updateHeader.CRMCompanyName = crmHeaders.CompanyName;
                updateHeader.CRMPictureURL = crmHeaders.AvatarURL;
            }

            return updateRequest;
        }

        private void TransactionTraceMessage(SIPTransaction sipTransaction, string message)
        {
        }

        private void FireCallTrying(SIPClientUserAgent uac, SIPResponse tryingResponse)
        {
            if (CallTrying != null)
            {
                CallTrying(uac, tryingResponse);
            }
        }

        private void FireCallRinging(SIPClientUserAgent uac, SIPResponse ringingResponse)
        {
            if (CallRinging != null)
            {
                CallRinging(uac, ringingResponse);
            }
        }

        private void FireCallAnswered(SIPClientUserAgent uac, SIPResponse answeredResponse)
        {
            if (CallAnswered != null)
            {
                CallAnswered(uac, answeredResponse);
            }
        }

        private void FireCallFailed(SIPClientUserAgent uac, string errorMessage)
        {
            if (CallFailed != null)
            {
                CallFailed(uac, errorMessage);
            }
        }
    }
}