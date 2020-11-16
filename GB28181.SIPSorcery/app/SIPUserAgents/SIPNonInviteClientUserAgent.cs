﻿// ============================================================================
// FileName: SIPNonInviteClientUserAgent.cs
//
// Description:
// A user agent that can send non-INVITE requests. The main need for this class is for
// sending non-INVITE requests that require authentication.
//
// Author(s):
// Aaron Clauson
//
// History:
// 26 Apr 2011	Aaron Clauson	Created.
//


using System;
using System.Threading;
using SIPSorcery.SIP;
using SIPSorcery.Sys;

namespace GB28181.App
{
    public class SIPNonInviteClientUserAgent
    {
        // private static ILog logger = AppState.logger;

        private static readonly string m_userAgent = SIPConstants.SIP_USERAGENT_STRING;


        private SIPTransport m_sipTransport;
        private SIPEndPoint m_outboundProxy;
        private SIPCallDescriptor m_callDescriptor;
        private string m_owner;
        private string m_adminMemberID;
        private string m_lastServerNonce;

        public event Action<SIPResponse> ResponseReceived;

        public SIPNonInviteClientUserAgent(
            SIPTransport sipTransport,
            SIPEndPoint outboundProxy,
            SIPCallDescriptor callDescriptor,
            string owner,
            string adminMemberID
        )
        {
            m_sipTransport = sipTransport;
            m_outboundProxy = outboundProxy;
            m_callDescriptor = callDescriptor;
            m_owner = owner;
            m_adminMemberID = adminMemberID;
        }

        public void SendRequest(SIPMethodsEnum method)
        {
            try
            {
                var req = GetRequest(method);
                var tran = m_sipTransport.CreateNonInviteTransaction(req, null, m_sipTransport.GetDefaultSIPEndPoint(),
                    m_outboundProxy);

                using var waitForResponse = new ManualResetEvent(false);
                tran.NonInviteTransactionTimedOut += RequestTimedOut;
                tran.NonInviteTransactionFinalResponseReceived += ServerResponseReceived;
                tran.SendReliableRequest();
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPNonInviteClientUserAgent SendRequest to " + m_callDescriptor.Uri +
                                    ". ->" +
                                    excp.Message);
                throw;
            }
        }

        private void RequestTimedOut(SIPTransaction sipTransaction)
        {
            Logger.Logger.Error("Exception SIPNonInviteClientUserAgent ReqeustTimeOut (" +
                                sipTransaction.RemoteEndPoint +
                                "). ");
        }

        private void ServerResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint,
            SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            try
            {
                string reasonPhrase = (sipResponse.ReasonPhrase.IsNullOrBlank())
                    ? sipResponse.Status.ToString()
                    : sipResponse.ReasonPhrase;

                if (sipResponse.Status == SIPResponseStatusCodesEnum.ProxyAuthenticationRequired ||
                    sipResponse.Status == SIPResponseStatusCodesEnum.Unauthorised)
                {
                    if (sipResponse.Header.AuthenticationHeader != null)
                    {
                        if ((m_callDescriptor.Username != null || m_callDescriptor.AuthUsername != null) &&
                            m_callDescriptor.Password != null)
                        {
                            SIPRequest authenticatedRequest =
                                GetAuthenticatedRequest(sipTransaction.TransactionRequest, sipResponse);
                            SIPNonInviteTransaction authTransaction =
                                m_sipTransport.CreateNonInviteTransaction(authenticatedRequest,
                                    sipTransaction.RemoteEndPoint, localSIPEndPoint, m_outboundProxy);
                            authTransaction.NonInviteTransactionFinalResponseReceived += AuthResponseReceived;
                            authTransaction.NonInviteTransactionTimedOut += RequestTimedOut;
                            m_sipTransport.SendSIPReliable(authTransaction);
                        }
                        else
                        {
                            if (ResponseReceived != null)
                            {
                                ResponseReceived(sipResponse);
                            }
                        }
                    }
                    else
                    {
                        if (ResponseReceived != null)
                        {
                            ResponseReceived(sipResponse);
                        }
                    }
                }
                else
                {
                    if (ResponseReceived != null)
                    {
                        ResponseReceived(sipResponse);
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPNonInviteClientUserAgent ServerResponseReceived (" + remoteEndPoint +
                                    "). ->" +
                                    excp.Message);
            }
        }

        /// <summary>
        /// The event handler for responses to the authenticated register request.
        /// </summary>
        private void AuthResponseReceived(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint,
            SIPTransaction sipTransaction, SIPResponse sipResponse)
        {
            string reasonPhrase = (sipResponse.ReasonPhrase.IsNullOrBlank())
                ? sipResponse.Status.ToString()
                : sipResponse.ReasonPhrase;

            if (ResponseReceived != null)
            {
                ResponseReceived(sipResponse);
            }
        }

        private SIPRequest GetRequest(SIPMethodsEnum method)
        {
            try
            {
                SIPURI uri = SIPURI.ParseSIPURIRelaxed(m_callDescriptor.Uri);

                SIPRequest request = new SIPRequest(method, uri);
                SIPFromHeader fromHeader = m_callDescriptor.GetFromHeader();
                fromHeader.FromTag = CallProperties.CreateNewTag();
                SIPToHeader toHeader = new SIPToHeader(null, uri, null);
                int cseq = Crypto.GetRandomInt(10000, 20000);

                SIPHeader header = new SIPHeader(fromHeader, toHeader, cseq, CallProperties.CreateNewCallId());
                header.CSeqMethod = method;
                header.UserAgent = m_userAgent;
                request.Header = header;

                SIPViaHeader viaHeader = new SIPViaHeader(m_sipTransport.GetDefaultSIPEndPoint(),
                    CallProperties.CreateBranchId());
                request.Header.Vias.PushViaHeader(viaHeader);

                try
                {
                    if (m_callDescriptor.CustomHeaders != null && m_callDescriptor.CustomHeaders.Count > 0)
                    {
                        foreach (string customHeader in m_callDescriptor.CustomHeaders)
                        {
                            if (customHeader.IsNullOrBlank())
                            {
                                continue;
                            }
                            else if (customHeader.Trim().StartsWith(SIPHeaders.SIP_HEADER_USERAGENT))
                            {
                                request.Header.UserAgent = customHeader.Substring(customHeader.IndexOf(":") + 1).Trim();
                            }
                            else
                            {
                                request.Header.UnknownHeaders.Add(customHeader);
                            }
                        }
                    }
                }
                catch (Exception excp)
                {
                    Logger.Logger.Error("Exception Parsing CustomHeader for SIPNonInviteClientUserAgent GetRequest. " +
                                        excp.Message + m_callDescriptor.CustomHeaders);
                }

                if (!m_callDescriptor.Content.IsNullOrBlank())
                {
                    request.Body = m_callDescriptor.Content;
                    request.Header.ContentType = m_callDescriptor.ContentType;
                    request.Header.ContentLength = request.Body.Length;
                }

                return request;
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPNonInviteClientUserAgent GetRequest. ->" + excp.Message);
                throw excp;
            }
        }

        private SIPRequest GetAuthenticatedRequest(SIPRequest originalRequest, SIPResponse sipResponse)
        {
            try
            {
                SIPAuthorisationDigest digest = sipResponse.Header.AuthenticationHeader.SIPDigest;
                m_lastServerNonce = digest.Nonce;
                string username = (m_callDescriptor.AuthUsername != null)
                    ? m_callDescriptor.AuthUsername
                    : m_callDescriptor.Username;
                digest.SetCredentials(username, m_callDescriptor.Password, originalRequest.URI.ToString(),
                    originalRequest.Method.ToString());

                SIPRequest authRequest = originalRequest.Copy();
                authRequest.LocalSIPEndPoint = originalRequest.LocalSIPEndPoint;
                authRequest.Header.Vias.TopViaHeader.Branch = CallProperties.CreateBranchId();
                authRequest.Header.From.FromTag = CallProperties.CreateNewTag();
                authRequest.Header.To.ToTag = null;
                authRequest.Header.CallId = CallProperties.CreateNewCallId();
                authRequest.Header.CSeq = originalRequest.Header.CSeq + 1;

                authRequest.Header.AuthenticationHeader = new SIPAuthenticationHeader(digest);
                authRequest.Header.AuthenticationHeader.SIPDigest.Response = digest.Digest;

                return authRequest;
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPNonInviteClientUserAgent GetAuthenticatedRequest. ->" + excp.Message);
                throw excp;
            }
        }
    }
}