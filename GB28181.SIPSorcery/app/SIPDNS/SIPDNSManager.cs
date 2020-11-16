﻿//-----------------------------------------------------------------------------
// Filename: SIPDNSManager.cs
//
// Description: An implementation of RFC 3263 to resolve SIP URIs using NAPTR, SRV and A records (could it be anymore convoluted?).
//
// History:
// 11 Mar 2009	Aaron Clauson	Created.
// 30 May 2020	Edward Chen     Updated.
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.


using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using SIPSorcery.SIP;

namespace GB28181.App
{
    /// <summary>
    /// 
    /// </summary>
    /// <remarks>
    /// 1. If transport parameter is specified it takes precedence,
    /// 2. If no transport parameter and target is an IP address then sip should use udp and sips tcp,
    /// 3. If no transport parameter and target is a host name with an explicit port then sip should use 
    ///    udp and sips tcp and host should be resolved using an A or AAAA record DNS lookup (section 4.2),
    /// 4. If no transport protocol and no explicit port and target is a host name then the client should no
    ///    an NAPTR lookup and utilise records for services SIP+D2U, SIP+D2T, SIP+D2S, SIPS+D2T and SIPS+D2S,
    /// 5. If NAPTR record(s) are found select the desired transport and lookup the SRV record,
    /// 6. If no NAPT records are found lookup SRV record for desired protocol _sip._udp, _sip._tcp, _sips._tcp,
    ///    _sip._tls,
    /// 7. If no SRV records found lookup A or AAAA record.
    /// 
    /// Observations from the field.
    /// - A DNS server has been observed to not respond at all to NAPTR or SRV record queries meaning lookups for
    ///   them will permanently time out.
    /// </remarks>
    public static class SIPDNSManager
    {
        private const int DNS_LOOKUP_TIMEOUT = 5; // 2 second timeout for DNS lookups.
        private const int DNS_A_RECORD_LOOKUP_TIMEOUT = 15; // 5 second timeout for crticial A record DNS lookups.
        private const int CACHE_UNAVAILABLE_SRV_LOOKUP_PERIOD = 300; // Period to cache timed or empty SRV lookups for.

        //   private static ILog logger = AppState.logger;

        private static int m_defaultSIPPort = SIPConstants.DEFAULT_SIP_PORT;
        private static int m_defaultSIPSPort = SIPConstants.DEFAULT_SIP_TLS_PORT;
        private static List<string> m_inProgressSIPServiceLookups = new List<string>();


        static SIPDNSManager()
        {
        }

        public static SIPDNSLookupResult ResolveSIPService(string host)
        {
            try
            {
                return ResolveSIPService(SIPURI.ParseSIPURIRelaxed(host), true);
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPDNSManager ResolveSIPService (" + host + "). ->" + excp.Message);
                throw;
            }
        }

        public static SIPDNSLookupResult ResolveSIPService(SIPURI sipURI, bool async)
        {
            try
            {
                if (sipURI == null)
                {
                    throw new ArgumentNullException("sipURI", "Cannot resolve SIP service on a null URI.");
                }

                string host = sipURI.Host;
                int port = (sipURI.Scheme == SIPSchemesEnum.sip) ? m_defaultSIPPort : m_defaultSIPSPort;
                bool explicitPort = false;

                if (sipURI.Host.IndexOf(':') != -1)
                {
                    host = sipURI.Host.Split(':')[0];
                    _ = int.TryParse(sipURI.Host.Split(':')[1], out port);
                    explicitPort = true;
                }

                if (Regex.Match(host, @"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$").Success)
                {
                    // Target is an IP address, no DNS lookup required.
                    IPAddress hostIP = IPAddress.Parse(host);
                    var sipLookupEndPoint =
                        new SIPDNSLookupEndPoint(new SIPEndPoint(sipURI.Protocol, new IPEndPoint(hostIP, port)), 0);
                    var result = new SIPDNSLookupResult(sipURI);
                    result.AddLookupResult(sipLookupEndPoint);
                    return result;
                }
                else if (explicitPort)
                {
                    // Target is a hostname with an explicit port, DNS lookup for A or AAAA record.
                    return DNSARecordLookup(host, port, async, sipURI);
                }
                else
                {
                    // Target is a hostname with no explicit port, use the whole NAPTR->SRV->A lookup procedure.
                    SIPDNSLookupResult sipLookupResult = new SIPDNSLookupResult(sipURI);

                    // Do without the NAPTR lookup for the time being. Very few organisations appear to use them and it can cost up to 2.5s to get a failed resolution.
                    /*SIPMonitorLogEvent(new SIPMonitorConsoleEvent(SIPMonitorServerTypesEnum.Unknown, SIPMonitorEventTypesEnum.DNS, "SIP DNS full lookup requested for " + sipURI.ToString() + ".", null));
                    DNSNAPTRRecordLookup(host, async, ref sipLookupResult);
                    if (sipLookupResult.Pending)
                    {
                        if (!m_inProgressSIPServiceLookups.Contains(sipURI.ToString()))
                        {
                            m_inProgressSIPServiceLookups.Add(sipURI.ToString());
                            ThreadPool.QueueUserWorkItem(delegate { ResolveSIPService(sipURI, false); });
                        }
                        return sipLookupResult;
                    }*/

                    DNSSRVRecordLookup(sipURI.Scheme, sipURI.Protocol, host, async, ref sipLookupResult);
                    if (sipLookupResult.Pending)
                    {
                        //logger.Debug("SIPDNSManager SRV lookup for " + host + " is pending.");
                        return sipLookupResult;
                    }
                    else
                    {
                        //logger.Debug("SIPDNSManager SRV lookup for " + host + " is final.");
                        SIPDNSServiceResult nextSRVRecord = sipLookupResult.GetNextUnusedSRV();
                        int lookupPort = (nextSRVRecord != null) ? nextSRVRecord.Port : port;
                        return DNSARecordLookup(nextSRVRecord, host, lookupPort, async, sipLookupResult.URI);
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPDNSManager ResolveSIPService (" + sipURI.ToString() + "). ->" +
                                    excp.Message);
                m_inProgressSIPServiceLookups.Remove(sipURI.ToString());
                return new SIPDNSLookupResult(sipURI, excp.Message);
            }
        }

        public static SIPDNSLookupResult DNSARecordLookup(string host, int port, bool async, SIPURI uri)
        {
            SIPDNSLookupResult result = new SIPDNSLookupResult(uri);


            return result;
        }

        public static SIPDNSLookupResult DNSARecordLookup(SIPDNSServiceResult nextSRVRecord, string host, int port,
            bool async, SIPURI lookupURI)
        {
            if (nextSRVRecord != null && nextSRVRecord.Data != null)
            {
                return DNSARecordLookup(nextSRVRecord.Data, port, async, lookupURI);
                //nextSRVRecord.ResolvedAt = DateTime.Now;
            }
            else
            {
                return DNSARecordLookup(host, port, async, lookupURI);
            }
        }

        public static void DNSSRVRecordLookup(SIPSchemesEnum scheme, SIPProtocolsEnum protocol, string host, bool async,
            ref SIPDNSLookupResult lookupResult)
        {
            SIPServicesEnum reqdNAPTRService = SIPServicesEnum.none;
            if (scheme == SIPSchemesEnum.sip && protocol == SIPProtocolsEnum.udp)
            {
                reqdNAPTRService = SIPServicesEnum.sipudp;
            }
            else if (scheme == SIPSchemesEnum.sip && protocol == SIPProtocolsEnum.tcp)
            {
                reqdNAPTRService = SIPServicesEnum.siptcp;
            }
            else if (scheme == SIPSchemesEnum.sips && protocol == SIPProtocolsEnum.tcp)
            {
                reqdNAPTRService = SIPServicesEnum.sipstcp;
            }
            else if (scheme == SIPSchemesEnum.sip && protocol == SIPProtocolsEnum.tls)
            {
                reqdNAPTRService = SIPServicesEnum.siptls;
            }

            // If there are NAPTR records available see if there is a matching one for the SIP scheme and protocol required.
            SIPDNSServiceResult naptrService = null;
            if (lookupResult.SIPNAPTRResults != null && lookupResult.SIPNAPTRResults.Count > 0)
            {
                if (reqdNAPTRService != SIPServicesEnum.none &&
                    lookupResult.SIPNAPTRResults.ContainsKey(reqdNAPTRService))
                {
                    naptrService = lookupResult.SIPNAPTRResults[reqdNAPTRService];
                }
            }

            // Construct the SRV target to lookup depending on whether an NAPTR record was available or not.
            string srvLookup = null;
            if (naptrService != null)
            {
                srvLookup = naptrService.Data;
            }
            else
            {
                srvLookup = "_" + scheme.ToString() + "._" + protocol.ToString() + "." + host;
            }
        }
    }
}