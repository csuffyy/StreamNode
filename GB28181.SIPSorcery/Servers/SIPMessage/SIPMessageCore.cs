﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GB28181.App;
using GB28181.Cache;
using GB28181.Config;
using GB28181.Net;
using GB28181.Net.RTP;
using GB28181.Servers.SIPMonitor;
using GB28181.Sys;
using GB28181.Sys.Model;
using GB28181.Sys.XML;
using SIPSorcery.SIP;

namespace GB28181.Servers.SIPMessage
{
    public struct MonitorKey
    {
        /// <summary>
        /// 设备编码
        /// </summary>
        public string DeviceID { get; set; }

        /// <summary>
        /// 命令类型
        /// </summary>
        public CommandType CmdType { get; set; }

        public override string ToString()
        {
            return this.DeviceID;
        }
    }

    /// <summary>
    /// sip消息核心处理
    /// </summary>
    public class SIPMessageCore : ISipMessageCore
    {
        #region 私有字段

        //  private static ILog logger = AppState.logger;

        private static string _sipServerAgent = SIPConstants.SIP_SERVER_STRING;
        public static int MEDIA_PORT_START = 10000;
        public static int MEDIA_PORT_END = 10000;
        public ISIPRegistrarCore _registrarCore;
        private ISipStorage _sipAccountStorage;
        private SIPAccount _LocalSipAccount;
        private ServiceStatus _serviceState;
        private SIPRequest _ackRequest;
        private SIPEndPoint _byeRemoteEP;
        private SIPResponse _audioResponse;

        public SIPAccount LocalSipAccount
        {
            get => _LocalSipAccount;
            set => _LocalSipAccount = value;
        }

        public ServiceStatus ServiceState
        {
            get => _serviceState;
            set => _serviceState = value;
        }

        /// <summary>
        /// SIP远端Host的Socket地址(host:Port)和用户名(GBID)
        /// </summary>
        private readonly Dictionary<string, string> _remoteTransEPs = new Dictionary<string, string>();

        public Dictionary<string, string> RemoteTransEPs => _remoteTransEPs;

        /// <summary>
        /// Monitor Service For all Remote Node
        /// </summary>
        private ConcurrentDictionary<string, ISIPMonitorCore> _nodeMonitorService =
            new ConcurrentDictionary<string, ISIPMonitorCore>();

        /// <summary>
        /// 用于控制设备下的摄像头，SipMonitorCore是控制核心
        /// </summary>
        public ConcurrentDictionary<string, ISIPMonitorCore> NodeMonitorService => _nodeMonitorService;


        /// <summary>
        /// 用于sip请求的缓存,某些需要有call-id对应的回复通过这个列表进行匹配
        /// </summary>
        private ConcurrentDictionary<string, SIPRequest> _sipSyncRequestContext =
            new ConcurrentDictionary<string, SIPRequest>();

        public ConcurrentDictionary<string, SIPRequest> SipSyncRequestContext => _sipSyncRequestContext;

        /// <summary>
        /// 本地sip终结点
        /// </summary>
        public SIPEndPoint LocalEP { get; set; }

        /// <summary>
        /// sip传输请求
        /// </summary>
        private ISIPTransport _transport;

        public ISIPTransport Transport => _transport;

        /// <summary>
        /// 本地域的sip编码
        /// </summary>
        public string LocalSIPId { get; set; }

        private Stream _g711Stream;
        private Channel _audioChannel;
        private IPEndPoint _audioRemoteEP;
        private AudioPSAnalyze _psAnalyze = new AudioPSAnalyze();
        private Data_Info_s packer = new Data_Info_s();
        private UInt32 src = 0; //算s64CurPts
        private UInt32 timestamp_increse = (UInt32) (90000.0 / 25);

        /// <summary>
        /// 用于保存通过设备目录查询而来的摄像头通道列表
        /// </summary>
        private IMemoCache<Camera> _cameraCache = null;

        public CancellationTokenSource _registryServiceToken = new CancellationTokenSource();

        #endregion

        #region 事件

        /// <summary>
        /// sip服务状态
        /// </summary>
        public event Action<string, ServiceStatus> OnServiceChanged;

        /// <summary>
        /// 录像文件接收
        /// </summary>
        public event Action<RecordInfo> OnRecordInfoReceived;

        /// <summary>
        /// 设备目录接收
        /// </summary>
        public event Action<Catalog> OnCatalogReceived;

        /// <summary>
        /// 设备目录通知
        /// </summary>
        public event Action<NotifyCatalog> OnNotifyCatalogReceived;

        /// <summary>
        /// 语音广播通知
        /// </summary>
        public event Action<VoiceBroadcastNotify> OnVoiceBroadcaseReceived;

        /// <summary>
        /// 报警通知
        /// </summary>
        public event Action<Alarm> OnAlarmReceived;

        /// <summary>
        /// 平台之间心跳接收
        /// </summary>
        public event Action<SIPEndPoint, KeepAlive, string> OnKeepaliveReceived;

        /// <summary>
        /// 设备状态查询接收
        /// </summary>
        public event Action<SIPEndPoint, DeviceStatus> OnDeviceStatusReceived;

        /// <summary>
        /// 设备信息查询接收
        /// </summary>
        public event Action<SIPEndPoint, DeviceInfo> OnDeviceInfoReceived;

        /// <summary>
        /// 设备配置查询接收
        /// </summary>
        public event Action<SIPEndPoint, DeviceConfigDownload> OnDeviceConfigDownloadReceived;

        /// <summary>
        /// 历史媒体发送结束接收
        /// </summary>
        public event Action<SIPEndPoint, MediaStatus> OnMediaStatusReceived;

        /// <summary>
        /// 响应状态码接收
        /// </summary>
        public event Action<SIPResponse, string, SIPEndPoint> OnResponseCodeReceived;

        /// <summary>
        /// 响应状态码接收
        /// </summary>
        public event Action<SIPResponse, SIPRequest, string, SIPEndPoint> OnResponseNeedResponeReceived;

        /// <summary>
        /// 预置位查询接收
        /// </summary>,
        public event Action<SIPEndPoint, PresetInfo> OnPresetQueryReceived;

        /// <summary>
        /// 设备注册时
        /// </summary>
        public event RegisterDelegate OnRegisterReceived;

        /// <summary>
        /// 设备注销时
        /// </summary>
        public event UnRegisterDelegate OnUnRegisterReceived;

        /// <summary>
        /// 设备有警告时
        /// </summary>
        public event DeviceAlarmSubscribeDelegate OnDeviceAlarmSubscribe;

        #endregion


        public SIPMessageCore(ISIPTransport sipTransport, string sipServerAgentStr)
        {
            _transport = sipTransport;
            _sipServerAgent = sipServerAgentStr;
            _transport.SIPTransportRequestReceived += AddMessageRequest;
            _transport.SIPTransportResponseReceived += AddMessageResponse;
            _cameraCache.OnItemAdded += _cameraCache_OnItemAdded;
        }


        public SIPMessageCore(
            ISIPTransport sipTransport,
            ISipStorage sipAccountStorage,
            IMemoCache<Camera> cameraCache)
        {
            _transport = sipTransport;
            _sipAccountStorage = sipAccountStorage;
            _LocalSipAccount = _sipAccountStorage.GetLocalSipAccout();
            _transport.SIPTransportRequestReceived += AddMessageRequest;
            _transport.SIPTransportResponseReceived += AddMessageResponse;
            _cameraCache = cameraCache;
            _cameraCache.OnItemAdded += _cameraCache_OnItemAdded;
            _registrarCore =
                new SIPRegistrarCore(_transport, sipAccountStorage, cameraCache, true, true);
            _registrarCore.DeviceAlarmSubscribe += OnDeviceAlarmSubscribeReceived;
            _registrarCore.RegisterReceived += _sipRegistrarCore_RegisterReceived;
            _registrarCore.UnRegisterReceived += _sipRegistrarCore_UnRegisterReceived;
            Task.Factory.StartNew(() => _registrarCore.ProcessRegisterRequest(), _registryServiceToken.Token);
        }

        /// <summary>
        /// 设备报警订阅回调
        /// </summary>
        /// <param name="sIPTransaction"></param>
        private void OnDeviceAlarmSubscribeReceived(SIPTransaction sIPTransaction)
        {
            if (OnDeviceAlarmSubscribe != null)
            {
                OnDeviceAlarmSubscribe.Invoke(sIPTransaction);
            }
        }

        /// <summary>
        /// 设备注册回调
        /// </summary>
        /// <param name="sipRequest"></param>
        /// <param name="sIPAccount"></param>
        private void _sipRegistrarCore_RegisterReceived(SIPRequest sipRequest, SIPAccount sIPAccount)
        {
            if (OnRegisterReceived != null)
            {
                OnRegisterReceived.Invoke(sipRequest, sIPAccount);
            }
        }


        /// <summary>
        /// 设备注销回调
        /// </summary>
        /// <param name="sipRequest"></param>
        /// <param name="sIPAccount"></param>
        private void _sipRegistrarCore_UnRegisterReceived(SIPRequest sipRequest, SIPAccount sIPAccount)
        {
            if (OnUnRegisterReceived != null)
            {
                OnUnRegisterReceived.Invoke(sipRequest, sIPAccount);
            }
        }

        public void Initialize(SIPAuthenticateRequestDelegate sipRequestAuthenticator,
            Dictionary<string, PlatformConfig> _platformList, SIPAccount account)
        {
        }

        private void _cameraCache_OnItemAdded(object arg1, Camera camera)
        {
            try
            {
                camera.Ctype = CameraType.GB28181;
                var ipaddress = IPAddress.Parse(camera.IPAddress);

                // 这里创建一了个摄像头的控制句柄 
                _nodeMonitorService.TryAdd(camera.DeviceID,
                    new SIPMonitorCore(this, _transport, sipAccountStorage: _sipAccountStorage)
                    {
                        RemoteEndPoint = new SIPEndPoint(SIPProtocolsEnum.udp, new IPEndPoint(ipaddress, camera.Port)),
                        DeviceId = camera.DeviceID
                    });
                //logger.Debug("_cameraCache_OnItemAdded: [" + camera.DeviceID + "," + camera.IPAddress.ToString() + ":" + camera.Port + "] item initialized.");
            }
            catch (Exception ex)
            {
                Logger.Logger.Error("Exception _cameraCache_OnItemAdded: ->" + ex.Message);
            }
        }

        #region 启动/停止消息主服务(监听注册链接)

        public void Start()
        {
            _serviceState = ServiceStatus.Wait;
            LocalEP = SIPEndPoint.ParseSIPEndPoint(_LocalSipAccount.MsgProtocol + ":" +
                                                   _LocalSipAccount.LocalIP.ToString() + ":" +
                                                   _LocalSipAccount.LocalPort);
            LocalSIPId = _LocalSipAccount.LocalID;

            try
            {
                Logger.Logger.Debug("SIPMessageCore is runing at " + LocalEP.ToString());
                var sipChannels = SIPTransportConfig.ParseSIPChannelsNode(_LocalSipAccount);
                _transport.PerformanceMonitorPrefix = SIPSorceryPerformanceMonitor.REGISTRAR_PREFIX;
                _transport.MsgEncode = _LocalSipAccount.MsgEncode;
                _transport.AddSIPChannel(sipChannels);
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception Start. ->" + excp.Message);
            }
        }

        public void Stop()
        {
            _sipSyncRequestContext.Clear();
            _cameraCache.Clear();
            //停掉所有摄像头控制
            foreach (var item in _nodeMonitorService)
            {
                item.Value.Stop();
            }

            LocalEP = null;
            _nodeMonitorService.Clear();
            _nodeMonitorService = null;
            if (_audioChannel != null)
            {
                _audioChannel.Stop();
            }

            try
            {
                Logger.Logger.Debug("SIP Registrar daemon stopping...");
                Logger.Logger.Debug("Shutting down SIP Transport.");

                _transport.Shutdown();
                _transport = null;

                Logger.Logger.Debug("sip message service stopped.");
                Logger.Logger.Debug("SIP Registrar daemon stopped.");
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception Stop. ->" + excp.Message);
            }
        }


        public void PtzControl(PTZCommand ptzcmd, int dwSpeed, string deviceId)
        {
            try
            {
                //找出设备，并对其进行控制
                foreach (var item in _nodeMonitorService.ToArray())
                {
                    if (item.Key.Equals(deviceId))
                    {
                        item.Value.PtzContrl(out string _, ptzcmd, dwSpeed);
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception PtzControl. ->" + excp.Message);
            }
        }

        public void DeviceStateQuery(string deviceId)
        {
            try
            {
                foreach (var item in _nodeMonitorService.ToArray())
                {
                    if (item.Key.Equals(deviceId))
                    {
                        item.Value.DeviceStateQuery();
                    }
                }
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception DeviceStateQuery. ->" + excp.Message);
            }
        }

        public int RecordFileQuery(string deviceId, DateTime startTime, DateTime endTime, string type)
        {
            int RecordTotal = 0;
            try
            {
                foreach (var item in _nodeMonitorService.ToArray())
                {
                    if (item.Key.Equals(deviceId))
                    {
                        RecordTotal = item.Value.RecordFileQuery(startTime, endTime, type);
                    }
                }

                Logger.Logger.Debug("RecordFileQuery halted.");
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception RecordFileQuery. ->" + excp.Message);
            }

            return RecordTotal;
        }

        #endregion

        #region 消息处理

        /// <summary>
        /// sip请求消息
        /// </summary>
        /// <param name="localEndPoint">本地终结点</param>
        /// <param name="remoteEndPoint"b>远程终结点</param>
        /// <param name="request">sip请求</param>
        public void AddMessageRequest(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            switch (request.Method)
            {
                case SIPMethodsEnum.REGISTER: //处理注册信息
                    RegisterHandle(localEP, remoteEP, request);
                    break;
                case SIPMethodsEnum.MESSAGE: //已知的是处理keepalive消息
                    MessageHandle(localEP, remoteEP, request);
                    break;
                case SIPMethodsEnum.NOTIFY:
                    NotifyHandle(localEP, remoteEP, request);
                    break;
                case SIPMethodsEnum.BYE:
                    SendResponse(localEP, remoteEP, request);
                    break;
                case SIPMethodsEnum.NONE:
                    break;
                case SIPMethodsEnum.UNKNOWN:
                    break;
                case SIPMethodsEnum.INVITE:
                    InviteHandle(localEP, remoteEP, request);
                    break;
                case SIPMethodsEnum.ACK:
                    AckHandle(localEP, remoteEP, request);
                    break;
                case SIPMethodsEnum.CANCEL:
                    break;
                case SIPMethodsEnum.OPTIONS:
                    break;
                case SIPMethodsEnum.INFO:
                    break;
                case SIPMethodsEnum.SUBSCRIBE:
                    break;
                case SIPMethodsEnum.PUBLISH:
                    break;
                case SIPMethodsEnum.PING:
                    break;
                case SIPMethodsEnum.REFER:
                    break;
                case SIPMethodsEnum.PRACK:
                    break;
                case SIPMethodsEnum.UPDATE:
                    break;
                default:
                    break;
            }
        }

        #region 音频请求处理

        /// <summary>
        ///  Invite请求消息
        /// </summary>
        /// <param name="localEP"></param>
        /// <param name="remoteEP"></param>
        /// <param name="request"></param>
        private void InviteHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            ///这里貌似被攻击了，有公网ip,不停的请求音频，导致本地端口注册失败，抛错，因为暂时用不着向上级平台级联，因此先不去理会这个请求，直接return掉
            return;
            try
            {
                Console.WriteLine("localEP->" + localEP.ToString());
                Console.WriteLine("remoteEP->" + remoteEP.ToString());
                Console.WriteLine("SIPRequest->" + request.ToString());
                _byeRemoteEP = remoteEP;
                int[] port = SetMediaPort();
                var trying = GetResponse(localEP, remoteEP, SIPResponseStatusCodesEnum.Trying, "", request);
                _transport.SendResponse(trying);
                SIPResponse audioOK = GetResponse(localEP, remoteEP, SIPResponseStatusCodesEnum.Ok, "", request);
                audioOK.Header.ContentType = "application/sdp";
                SIPURI localUri = new SIPURI(LocalSIPId, LocalEP.ToHost(), "");
                SIPContactHeader contact = new SIPContactHeader(null, localUri);
                audioOK.Header.Contact = new List<SIPContactHeader>
                {
                    contact
                };
                //SDP
                audioOK.Body = SetMediaAudio(localEP.Address.ToString(), port[0], request.URI.User);
                _audioResponse = audioOK;
                _transport.SendResponse(audioOK);
                int recvPort = GetReceivePort(request.Body, SDPMediaTypesEnum.audio);
                string ip = GetReceiveIP(request.Body);
                _audioRemoteEP = new IPEndPoint(IPAddress.Parse(ip), recvPort);
                if (_audioChannel == null)
                {
                    _audioChannel = new UDPChannel(TcpConnectMode.active, IPAddress.Any, port, ProtocolType.Udp, false,
                        recvPort);
                }

                _audioChannel.Start();
            }
            catch (Exception ex)
            {
                Console.WriteLine("InviteHandle:\r\n" + ex.Message + "\r\n" + ex.StackTrace);
            }
        }

        public void AckHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            _ackRequest = request;
            if (_g711Stream == null)
            {
                _g711Stream = new FileStream("D:\\audio.g711", FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            new Thread(new ThreadStart(SendRtp)).Start();
        }

        private void SendRtp()
        {
            packer.sendSOcket = _audioChannel.SendSocket;
            packer.IFrame = 0;
            packer.u32Ssrc = 0x0857;
            packer.s64CurPts = src;
            packer.remotePoint = _audioRemoteEP;
            int i = 0;
            long offset = 240;
            while (_g711Stream.Length > i)
            {
                byte[] buffer = new byte[240];
                _g711Stream.Read(buffer, 0, buffer.Length);
                _psAnalyze.Gb28181_streampackageForH264(buffer, buffer.Length, packer, 1);
                _g711Stream.Seek(offset, SeekOrigin.Begin);
                Thread.Sleep(5);
                i += buffer.Length;
                offset += buffer.Length;
                src += timestamp_increse;
            }

            ByeVideoReq();
        }

        public void ByeVideoReq()
        {
            if (_audioChannel == null)
            {
                return;
            }

            _audioChannel.Stop();
            _audioChannel = null;

            SIPURI localUri = new SIPURI(_LocalSipAccount.SIPPassword, LocalEP.ToHost(), "");
            SIPURI remoteUri = new SIPURI(_ackRequest.Header.From.FromURI.User, _byeRemoteEP.ToHost(), "");
            SIPFromHeader from = new SIPFromHeader(null, localUri, _ackRequest.Header.To.ToTag);
            SIPToHeader to = new SIPToHeader(null, remoteUri, _ackRequest.Header.From.FromTag);
            SIPRequest byeReq = new SIPRequest(SIPMethodsEnum.BYE, localUri);
            SIPHeader header = new SIPHeader(from, to, _ackRequest.Header.CSeq + 1, _ackRequest.Header.CallId)
            {
                CSeqMethod = SIPMethodsEnum.BYE
            };

            SIPViaHeader viaHeader = new SIPViaHeader(LocalEP, CallProperties.CreateBranchId())
            {
                Branch = CallProperties.CreateBranchId(),
                Transport = SIPProtocolsEnum.udp
            };
            SIPViaSet viaSet = new SIPViaSet
            {
                Via = new List<SIPViaHeader>()
            };
            viaSet.Via.Add(viaHeader);
            header.Vias = viaSet;

            header.UserAgent = SIPConstants.SIP_USERAGENT_STRING;
            header.Contact = _ackRequest.Header.Contact;
            byeReq.Header = header;
            SendRequest(_byeRemoteEP, byeReq);
        }


        private byte[] RtpG711Packet(byte[] buffer, ushort seq, uint timestamp)
        {
            RTPHeader header = new RTPHeader
            {
                Version = 2,
                PaddingFlag = 0,
                HeaderExtensionFlag = 0,
                CSRCCount = 0,
                MarkerBit = 1,
                PayloadType = 0x88,
                SequenceNumber = seq,
                Timestamp = timestamp,
                SyncSource = 0x0857,
            };

            RTPPacket packet = new RTPPacket
            {
                Header = header,
                Payload = buffer
            };
            byte[] newBuffer = new byte[12 + buffer.Length];
            Buffer.BlockCopy(header.GetBytes(), 0, newBuffer, 0, 12);
            Buffer.BlockCopy(buffer, 0, newBuffer, 12, buffer.Length);
            return newBuffer;
        }

        /// <summary>
        /// 设置媒体参数请求(实时)
        /// </summary>
        /// <param name="localIp">本地ip</param>
        /// <param name="mediaPort">rtp/rtcp媒体端口(10000/10001)</param>
        /// <returns></returns>
        private string SetMediaAudio(string localIp, int port, string audioId)
        {
            var sdpConn = new SDPConnectionInformation(localIp);

            var sdp = new SDP
            {
                Version = 0,
                SessionId = "0",
                Username = audioId,
                SessionName = CommandType.Play.ToString(),
                Connection = sdpConn,
                Timing = "0 0",
                Address = localIp
            };

            var psFormat = new SDPMediaFormat(SDPMediaFormatsEnum.PS)
            {
                IsStandardAttribute = false
            };

            var media = new SDPMediaAnnouncement
            {
                Media = SDPMediaTypesEnum.audio
            };

            media.MediaFormats.Add(psFormat);
            media.AddExtra("a=sendonly");
            media.AddExtra("y=0100000002");
            //media.AddExtra("f=v/////a/1/8/1");
            media.AddFormatParameterAttribute(psFormat.FormatID, psFormat.Name);
            media.Port = port;

            sdp.Media.Add(media);

            return sdp.ToString();
        }

        #endregion

        /// <summary>
        /// SIP响应消息
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="response">sip响应</param>
        public void AddMessageResponse(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPResponse response)
        {
            if (response == null)
            {
                Logger.Logger.Error("对方回复为空(response == null)");
                return;
            }

            var obj = _nodeMonitorService[response.Header.To.ToURI.User];
            if (obj != null)
            {
                if (((SIPMonitorCore) obj).SyncRequestContext.ContainsKey(response.Header.CallId))
                {
                    SIPRequest request = ((SIPMonitorCore) obj).SyncRequestContext[response.Header.CallId];
                    ((SIPMonitorCore) obj).SyncRequestContext.TryRemove(response.Header.CallId, out _);
                    OnResponseNeedResponeReceived?.Invoke(response, request, "收到需要回复的一些信息", remoteEP);
                }
            }


            if (SipSyncRequestContext.ContainsKey(response.Header.CallId))
            {
                SIPRequest request = SipSyncRequestContext[response.Header.CallId];
                SipSyncRequestContext.TryRemove(response.Header.CallId, out _);
                OnResponseNeedResponeReceived?.Invoke(response, request, "收到需要回复的一些信息", remoteEP);
            }


            if (response.Status == SIPResponseStatusCodesEnum.Ok)
            {
                //add by zohn on 20180711
                if (response.Header.ContentType == null)
                {
                    return;
                }

                if (response.Header.CSeqMethod == SIPMethodsEnum.SUBSCRIBE)
                {
                    //订阅消息
                    _nodeMonitorService[response.Header.To.ToURI.User].Subscribe(response);
                }
                else if (response.Header.ContentType.ToLower() == SIPHeader.ContentTypes.Application_SDP)
                {
                    var cmdType = GetCmdTypeFromSDP(response.Body);

                    if (cmdType == CommandType.Unknown)
                    {
                        OnResponseCodeReceived(response, "接收到未知的SIP消息(application/sdp)", remoteEP);
                    }

                    if (cmdType == CommandType.Download)
                    {
                        cmdType = CommandType.Playback;
                    }

                    lock (_nodeMonitorService) //这里有实时视频请求的回复信息
                    {
                        _nodeMonitorService[response.Header.To.ToURI.User].AckRequest(response); //可能需要回复对方
                    }
                }

                OnResponseCodeReceived?.Invoke(response, "对方国标平台返回状态【成功】", remoteEP); //回调一下
            }
            else //如果回复是失败的
            {
                switch (response.Status)
                {
                    case SIPResponseStatusCodesEnum.BadRequest: //请求失败
                    case SIPResponseStatusCodesEnum.InternalServerError: //服务器内部错误
                    case SIPResponseStatusCodesEnum.RequestTerminated: //请求终止
                    case SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist: //呼叫/事务不存在
                        OnResponseCodeReceived?.Invoke(response, "对方国标平台返回状态【失败】", remoteEP);
                        break;
                    case SIPResponseStatusCodesEnum.Trying: //等待处理
                        OnResponseCodeReceived?.Invoke(response, "对方国标平台返回状态【正在尝试】", remoteEP);
                        break;
                    default:
                        OnResponseCodeReceived?.Invoke(response, "对方国标平台返回状态【其他】", remoteEP);
                        break;
                }
            }
        }

        //这个函数的意义是什么？意义并不大
        public void OnSIPServiceChange(string msg, ServiceStatus state)
        {
            _serviceState = ServiceStatus.Complete;

            try
            {
                OnServiceChanged.Invoke(msg, state);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 注册消息处理
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        private void RegisterHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            OnSIPServiceChange(remoteEP.ToHost(), ServiceStatus.Complete); //为什么要先做一次服务状态修改？
            lock (_remoteTransEPs)
            {
                if (!_remoteTransEPs.ContainsKey(remoteEP.ToHost()))
                {
                    _remoteTransEPs.Add(remoteEP.ToHost(), request.Header.From.FromURI.User); //加入
                    Logger.Logger.Debug("加入RemoteTransEps->" + remoteEP.ToHost() + "->" +
                                        request.Header.From.FromURI.User);
                }
            }

            _registrarCore.AddRegisterRequest(localEP, remoteEP, request); //做注册请求处理
        }

        /// <summary>
        /// 目录查询响应消息处理
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        /// <param name="catalog">目录结构体</param>
        private void CatalogHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request, Catalog catalog)
        {
            try
            {
                catalog.CallId = request.Header.CallId;
                catalog.RemoteEP = remoteEP.ToHost();
                catalog.DeviceList.Items.FindAll(item => item != null).ForEach(catalogItem =>
                {
                    catalogItem.RemoteEP = remoteEP.ToHost();
                    var devCata = DevType.CataTypeGetCataType(catalogItem.DeviceID);
                    if (devCata == DevCataType.Device)
                    {
                        if (!_nodeMonitorService.ContainsKey(catalogItem.DeviceID))
                        {
                            _nodeMonitorService.TryAdd(catalogItem.DeviceID,
                                new SIPMonitorCore(this, _transport, _sipAccountStorage)
                                {
                                    RemoteEndPoint = remoteEP,
                                    DeviceId = catalogItem.DeviceID
                                });
                        }
                    }
                });
                OnCatalogReceived?.Invoke(catalog);
            }
            catch (Exception ex)
            {
                Logger.Logger.Error("CatalogHandle Exception. ->" + ex.Message);
            }
        }

        /// <summary>
        /// 心跳消息处理
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        /// <param name="keepAlive">心跳结构体</param>
        private void KeepaliveHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request, KeepAlive keepAlive)
        {
            // SendResponse(localEP, remoteEP, request);
            OnSIPServiceChange(remoteEP.ToHost(), ServiceStatus.Complete);
            lock (_remoteTransEPs)
            {
                if (!_remoteTransEPs.ContainsKey(remoteEP.ToHost()))
                {
                    SendResponseWithError(localEP, remoteEP, request); //如果心跳设备都不在列表中，就返错给设备
                }
                else
                {
                    SendResponse(localEP, remoteEP, request);
                    OnKeepaliveReceived?.Invoke(remoteEP, keepAlive, request.Header.From.FromURI.User); //回调出去
                }
            }
        }

        /// <summary>
        /// 录像查询消息处理
        /// </summary>
        /// <param name="localSIPEndPoint">本地终结点</param>
        /// <param name="remoteEndPoint">远程终结点</param>
        /// <param name="response">sip响应</param>
        /// <param name="record">录像xml结构体</param>
        private void RecordInfoHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request, RecordInfo record)
        {
            _nodeMonitorService[record.DeviceID].RecordQueryTotal(record.SumNum);
            if (OnRecordInfoReceived != null && record.RecordItems != null)
            {
                OnRecordInfoReceived(record);
            }
        }

        /// <summary>
        /// Message消息处理
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        private void MessageHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            //心跳消息
            KeepAlive keepAlive = KeepAlive.Instance.Read(request.Body);
            if (keepAlive != null && keepAlive.CmdType == CommandType.Keepalive)
            {
                KeepaliveHandle(localEP, remoteEP, request, keepAlive);
                return;
            }

            SendResponse(localEP, remoteEP, request); //除了心跳，其他的内容先返正确消息给设备

            //设备目录
            Catalog catalog = Catalog.Instance.Read(request.Body);
            if (catalog != null && catalog.CmdType == CommandType.Catalog)
            {
                CatalogHandle(localEP, remoteEP, request, catalog);
                return;
            }

            //录像查询
            RecordInfo record = RecordInfo.Instance.Read(request.Body);
            if (record != null && record.CmdType == CommandType.RecordInfo)
            {
                RecordInfoHandle(localEP, remoteEP, request, record);
                return;
            }

            //媒体通知
            MediaStatus mediaStatus = MediaStatus.Instance.Read(request.Body);
            if (mediaStatus != null && mediaStatus.CmdType == CommandType.MediaStatus)
            {
                _nodeMonitorService[request.Header.From.FromURI.User].ByeVideoReq(out string _);
                //取值121表示历史媒体文件发送结束（回放结束/下载结束）
                //NotifyType未找到相关文档标明所有该类型值，暂时只处理121
                if (mediaStatus.NotifyType.Equals("121"))
                {
                    OnMediaStatusReceived?.Invoke(remoteEP, mediaStatus);
                }

                return;
            }

            //设备状态查询
            DeviceStatus deviceStatus = DeviceStatus.Instance.Read(request.Body);
            if (deviceStatus != null && deviceStatus.CmdType == CommandType.DeviceStatus)
            {
                OnDeviceStatusReceived?.Invoke(remoteEP, deviceStatus);
                return;
            }

            //设备信息查询
            DeviceInfo deviceInfo = DeviceInfo.Instance.Read(request.Body);
            if (deviceInfo != null && deviceInfo.CmdType == CommandType.DeviceInfo)
            {
                OnDeviceInfoReceived?.Invoke(remoteEP, deviceInfo);
                return;
            }

            //设备配置查询
            DeviceConfigDownload devDownload = DeviceConfigDownload.Instance.Read(request.Body);
            if (devDownload != null && devDownload.CmdType == CommandType.ConfigDownload)
            {
                OnDeviceConfigDownloadReceived?.Invoke(remoteEP, devDownload);
            }

            //预置位查询
            PresetInfo preset = PresetInfo.Instance.Read(request.Body);
            if (preset != null && preset.CmdType == CommandType.PresetQuery)
            {
                OnPresetQueryReceived?.Invoke(remoteEP, preset);
            }

            //报警通知
            Alarm alarm = Alarm.Instance.Read(request.Body);
            if (alarm != null && alarm.CmdType == CommandType.Alarm) //单兵上报经纬度
            {
                OnAlarmReceived?.Invoke(alarm);
            }
        }

        /// <summary>
        /// 目录订阅通知消息处理
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        private void NotifyHandle(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            SendResponse(localEP, remoteEP, request);
            //目录推送通知
            NotifyCatalog notify = NotifyCatalog.Instance.Read(request.Body);
            if (notify != null && notify.CmdType == CommandType.Catalog) //设备目录
            {
                OnNotifyCatalogReceived?.Invoke(notify);
            }

            //语音广播通知
            //移动设备位置数据通知
            VoiceBroadcastNotify voice = VoiceBroadcastNotify.Instance.Read(request.Body);
            if (voice != null && voice.CmdType == CommandType.Broadcast) //语音广播
            {
                OnVoiceBroadcaseReceived?.Invoke(voice);
            }
        }

        /// <summary>
        /// 发送异常的sip响应信息
        /// </summary>
        /// <param name="localEP"></param>
        /// <param name="remoteEP"></param>
        /// <param name="request"></param>
        private void SendResponseWithError(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            if (_serviceState == ServiceStatus.Wait)
            {
                OnSIPServiceChange(remoteEP.ToHost(), ServiceStatus.Wait);
                return;
            }

            SIPResponse res = GetResponse(localEP, remoteEP, SIPResponseStatusCodesEnum.BadRequest, "", request);
            _transport.SendResponse(res);
        }


        /// <summary>
        /// 发送sip响应消息
        /// </summary>
        /// <param name="localEP">本地终结点</param>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        private void SendResponse(SIPEndPoint localEP, SIPEndPoint remoteEP, SIPRequest request)
        {
            if (_serviceState == ServiceStatus.Wait)
            {
                OnSIPServiceChange(remoteEP.ToHost(), ServiceStatus.Wait);
                return;
            }

            SIPResponse res = GetResponse(localEP, remoteEP, SIPResponseStatusCodesEnum.Ok, "", request);
            _transport.SendResponse(res);
        }

        /// <summary>
        /// 发送sip请求消息
        /// </summary>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求</param>
        public void SendRequest(SIPEndPoint remoteEP, SIPRequest request)
        {
            if (_serviceState == ServiceStatus.Wait)
            {
                OnSIPServiceChange(remoteEP.ToHost(), ServiceStatus.Wait);
                return;
            }

            _transport.SendRequest(remoteEP, request);
        }

        /// <summary>
        /// 发送可靠的sip请求消息
        /// </summary>
        /// <param name="remoteEP">远程终结点</param>
        /// <param name="request">sip请求消息</param>
        public void SendReliableRequest(SIPEndPoint remoteEP, SIPRequest request)
        {
            if (_serviceState == ServiceStatus.Wait)
            {
                OnSIPServiceChange(remoteEP.ToHost(), ServiceStatus.Wait);
                return;
            }

            var transaction = _transport.CreateUASTransaction(request, remoteEP, LocalEP, null);
            transaction.TransactionStateChanged += Trans_TransactionStateChanged;
            transaction.SendReliableRequest();
        }

        private void Trans_TransactionStateChanged(SIPTransaction transaction)
        {
            AddMessageResponse(transaction.LocalSIPEndPoint, transaction.RemoteEndPoint,
                transaction.TransactionFinalResponse);
        }

        #endregion

        #region SDP消息处理

        public string GetReceiveIP(string sdpStr)
        {
            string[] sdpLines = sdpStr.Split('\n');

            var targetLine = sdpLines.FirstOrDefault(line => line.Trim().StartsWith("c="));

            if (targetLine != null)
            {
                var conn = SDPConnectionInformation.ParseConnectionInformation(targetLine);
                return conn.ConnectionAddress;
            }

            return null;
        }

        /// <summary>
        /// 获取接收方端口号码
        /// </summary>
        /// <param name="sdpStr">SDP</param>
        /// <returns></returns>
        public int GetReceivePort(string sdpStr, SDPMediaTypesEnum mediaType)
        {
            string[] sdpLines = sdpStr.Split('\n');

            foreach (var line in sdpLines)
            {
                if (line.Trim().StartsWith("m="))
                {
                    Match mediaMatch = Regex.Match(line.Substring(2).Trim(),
                        @"(?<type>\w+)\s+(?<port>\d+)\s+(?<transport>\S+)\s+(?<formats>.*)$");
                    if (mediaMatch.Success)
                    {
                        SDPMediaAnnouncement announcement = new SDPMediaAnnouncement
                        {
                            Media = SDPMediaTypes.GetSDPMediaType(mediaMatch.Result("${type}"))
                        };
                        Int32.TryParse(mediaMatch.Result("${port}"), out announcement.Port);
                        announcement.Transport = mediaMatch.Result("${transport}");
                        announcement.ParseMediaFormats(mediaMatch.Result("${formats}"));
                        if (announcement.Media != mediaType)
                        {
                            continue;
                        }

                        return announcement.Port;
                    }
                }
            }

            return 0;
        }


        private CommandType GetCmdTypeFromSDP(string body)
        {
            var targetCmdType = CommandType.Unknown;
            var textLines = body.Replace("\r", "").Split('\n');

            foreach (var item in textLines)
            {
                var values = item.Split('=');
                if (values.Contains("s"))
                {
                    var tmpstr = values[1].ToLower();
                    if (tmpstr.Contains("back"))
                    {
                        targetCmdType = CommandType.Playback;
                    }
                    else if (tmpstr.Contains("play") || tmpstr.Contains("ipc"))
                    {
                        targetCmdType = CommandType.Play;
                    }
                }
                else if (values.Contains("t"))
                {
                    if (values[1].Replace(" ", "") == "00")
                    {
                        targetCmdType = CommandType.Play;
                    }
                    else
                    {
                        targetCmdType = CommandType.Playback;
                    }
                }
            }

            return targetCmdType;
        }

        /// <summary>
        /// 获取SDP协议中SessionName字段值
        /// </summary>
        /// <param name="body">SDP文本</param>
        /// <returns></returns>
        private string GetSessionName(string body)
        {
            var textLines = body.Replace("\r", "").Split('\n');
            var targetline = textLines.FirstOrDefault(item =>
            {
                var values = item.Split('=');
                var tmpstr = values[1].ToLower();
                return tmpstr.Contains("play") || tmpstr.Contains("download") || tmpstr.Contains("ipc");
            });
            return targetline?.Replace(" ", "");
        }

        #endregion

        #region 响应消息

        private SIPResponse GetResponse(SIPEndPoint localSIPEndPoint, SIPEndPoint remoteEndPoint,
            SIPResponseStatusCodesEnum responseCode, string reasonPhrase, SIPRequest request)
        {
            try
            {
                SIPResponse response = new SIPResponse(responseCode, reasonPhrase, localSIPEndPoint);
                SIPSchemesEnum sipScheme = (localSIPEndPoint.Protocol == SIPProtocolsEnum.tls)
                    ? SIPSchemesEnum.sips
                    : SIPSchemesEnum.sip;
                SIPFromHeader from = request.Header.From;
                from.FromTag = request.Header.From.FromTag;
                SIPToHeader to = request.Header.To;
                response.Header = new SIPHeader(from, to, request.Header.CSeq, request.Header.CallId)
                {
                    CSeqMethod = request.Header.CSeqMethod,
                    Vias = request.Header.Vias,
                    UserAgent = SIPConstants.SIP_USERAGENT_STRING,
                    CSeq = request.Header.CSeq
                };

                if (response.Header.To.ToTag == null || request.Header.To.ToTag.Trim().Length == 0)
                {
                    response.Header.To.ToTag = CallProperties.CreateNewTag();
                }

                return response;
            }
            catch (Exception excp)
            {
                Logger.Logger.Error("Exception SIPTransport GetResponse. ->" + excp.Message);
                throw;
            }
        }

        #endregion

        #region 目录查询/订阅

        /// <summary>
        /// 设备目录查询
        /// </summary>
        /// <param name="deviceId">目的设备编码</param>
        public void DeviceCatalogQuery()
        {
            lock (_remoteTransEPs)
            {
                foreach (var item in _remoteTransEPs)
                {
                    SIPEndPoint remoteEP = SIPEndPoint.ParseSIPEndPoint("udp:" + item.Key);
                    string callid = "";
                    SIPRequest catalogReq = QueryItems(remoteEP, item.Value, out callid);
                    CatalogQuery catalog = new CatalogQuery()
                    {
                        CommandType = CommandType.Catalog,
                        DeviceID = item.Value,
                        SN = new Random().Next(1, ushort.MaxValue)
                    };
                    string xmlBody = CatalogQuery.Instance.Save<CatalogQuery>(catalog);
                    catalogReq.Body = xmlBody;
                    SendRequest(remoteEP, catalogReq);
                }
            }
        }

        /// <summary>
        /// 设备目录查询
        /// </summary>
        /// <param name="deviceId">目的设备编码</param>
        public void DeviceCatalogQuery(string deviceId, out string cid, bool needResult = false)
        {
            cid = "";
            lock (_remoteTransEPs)
            {
                foreach (var item in _remoteTransEPs)
                {
                    if (item.Value == deviceId)
                    {
                        SIPEndPoint remoteEP = SIPEndPoint.ParseSIPEndPoint("udp:" + item.Key);
                        string callid = "";
                        SIPRequest catalogReq = QueryItems(remoteEP, item.Value, out callid);
                        cid = callid;
                        CatalogQuery catalog = new CatalogQuery()
                        {
                            CommandType = CommandType.Catalog,
                            DeviceID = item.Value,
                            SN = new Random().Next(1, ushort.MaxValue)
                        };
                        string xmlBody = CatalogQuery.Instance.Save<CatalogQuery>(catalog);
                        catalogReq.Body = xmlBody;
                        if (needResult)
                        {
                            SipSyncRequestContext.TryAdd(catalogReq.Header.CallId, catalogReq);
                        }

                        SendRequest(remoteEP, catalogReq);
                    }
                }
            }
        }

        /// <summary>
        /// 查询设备目录请求
        /// </summary>
        /// <returns></returns>
        private SIPRequest QueryItems(SIPEndPoint remoteEndPoint, string remoteSIPId, out string callid)
        {
            string fromTag = CallProperties.CreateNewTag();
            string toTag = CallProperties.CreateNewTag();
            int cSeq = CallHelpers.CreateNewCSeq();
            string callId = CallProperties.CreateNewCallId();
            callid = callId;
            SIPURI remoteUri = new SIPURI(remoteSIPId, remoteEndPoint.ToHost(), "");
            SIPURI localUri = new SIPURI(LocalSIPId, LocalEP.ToHost(), "");
            SIPFromHeader from = new SIPFromHeader(null, localUri, fromTag);
            SIPToHeader to = new SIPToHeader(null, remoteUri, null);
            SIPRequest catalogReq = _transport.GetRequest(SIPMethodsEnum.MESSAGE, remoteUri);
            catalogReq.Header.From = from;
            catalogReq.Header.Contact = null;
            catalogReq.Header.Allow = null;
            catalogReq.Header.To = to;
            catalogReq.Header.UserAgent = SIPConstants.SIP_USERAGENT_STRING;
            catalogReq.Header.CSeq = cSeq;
            catalogReq.Header.CallId = callId;
            catalogReq.Header.ContentType = "application/MANSCDP+xml";

            return catalogReq;
        }

        /// <summary>
        /// 目录订阅
        /// </summary>
        /// <param name="deviceId">目的设备编码</param>
        public void DeviceCatalogSubscribe(string deviceId)
        {
            lock (_remoteTransEPs)
            {
                foreach (var item in _remoteTransEPs)
                {
                    if (item.Value == deviceId)
                    {
                        SIPEndPoint remoteEP = SIPEndPoint.ParseSIPEndPoint("udp:" + item.Key);
                        DeviceCatalogSubscribe(remoteEP, deviceId);
                    }
                }
            }
        }

        /// <summary>
        /// 目录订阅
        /// </summary>
        /// <param name="deviceId">目的设备编码</param>
        public void DeviceCatalogSubscribe(SIPEndPoint remoteEP, string remoteID)
        {
            SIPRequest catalogReq = SubscribeCatalog(remoteEP, remoteID);
            CatalogQuery catalog = new CatalogQuery()
            {
                CommandType = CommandType.Catalog,
                DeviceID = remoteID,
                SN = new Random().Next(1, ushort.MaxValue)
            };
            string xmlBody = CatalogQuery.Instance.Save<CatalogQuery>(catalog);
            catalogReq.Body = xmlBody;
            SendRequest(remoteEP, catalogReq);
        }

        /// <summary>
        /// 目录订阅请求
        /// </summary>
        /// <returns></returns>
        private SIPRequest SubscribeCatalog(SIPEndPoint remoteEndPoint, string remoteSIPId)
        {
            string fromTag = CallProperties.CreateNewTag();
            int cSeq = CallHelpers.CreateNewCSeq();
            string callId = CallProperties.CreateNewCallId();

            SIPURI remoteUri = new SIPURI(remoteSIPId, remoteEndPoint.ToHost(), "");
            SIPURI localUri = new SIPURI(LocalSIPId, LocalEP.ToHost(), "");
            SIPFromHeader from = new SIPFromHeader(null, localUri, fromTag);
            SIPToHeader to = new SIPToHeader(null, remoteUri, null);
            SIPRequest catalogReq = _transport.GetRequest(SIPMethodsEnum.SUBSCRIBE, remoteUri);

            catalogReq.Header.From = from;
            catalogReq.Header.Contact = new List<SIPContactHeader>
            {
                new SIPContactHeader(null, localUri)
            };
            catalogReq.Header.Allow = null;
            catalogReq.Header.To = to;
            catalogReq.Header.UserAgent = SIPConstants.SIP_USERAGENT_STRING;
            catalogReq.Header.Event = "Catalog";
            catalogReq.Header.Expires = 60000;
            catalogReq.Header.CSeq = cSeq;
            catalogReq.Header.CallId = callId;
            catalogReq.Header.ContentType = "application/MANSCDP+xml";

            return catalogReq;
        }

        #endregion

        #region 移动设备位置数据订阅

        public void MobileDataSubscription(string devID)
        {
            lock (_remoteTransEPs)
            {
                foreach (var item in _remoteTransEPs)
                {
                    SIPEndPoint remoteEP = SIPEndPoint.ParseSIPEndPoint("udp:" + item.Key);
                    string callid = "";
                    SIPRequest eventSubscribeReq = QueryItems(remoteEP, devID, out callid);
                    //eventSubscribeReq.Method = SIPMethodsEnum.SUBSCRIBE;
                    CatalogQuery catalog = new CatalogQuery()
                    {
                        CommandType = CommandType.MobilePosition,
                        DeviceID = devID,
                        SN = new Random().Next(1, ushort.MaxValue)
                    };
                    string xmlBody = CatalogQuery.Instance.Save<CatalogQuery>(catalog);
                    eventSubscribeReq.Body = xmlBody;
                    SendRequest(remoteEP, eventSubscribeReq);
                }
            }
        }

        #endregion

        #region 设置媒体流端口号

        /// <summary>
        /// 设置媒体(rtp/rtcp)端口号
        /// </summary>
        /// <returns></returns>
        public int[] SetMediaPort()
        {
            int[] mediaPort = new int[2];
            if (MEDIA_PORT_END == MEDIA_PORT_START && MEDIA_PORT_START == _LocalSipAccount.MediaPort)
            {
                mediaPort[0] = _LocalSipAccount.MediaPort;
                mediaPort[1] = _LocalSipAccount.MediaPort;
            }
            else
            {
                var inUseUDPPorts =
                    (from p in IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
                        where p.Port >= MEDIA_PORT_START
                        select p.Port).OrderBy(x => x).ToList();


                int rtpPort = 0;
                int rtcpPort = 0;

                if (inUseUDPPorts.Count > 0)
                {
                    // Find the first two available for the RTP socket.
                    for (int index = MEDIA_PORT_START; index <= MEDIA_PORT_END; index++)
                    {
                        if (!inUseUDPPorts.Contains(index))
                        {
                            rtpPort = index;
                            break;
                        }
                    }

                    // Find the next available for the control socket.
                    for (int index = rtpPort + 1; index <= MEDIA_PORT_END; index++)
                    {
                        if (!inUseUDPPorts.Contains(index))
                        {
                            rtcpPort = index;
                            break;
                        }
                    }
                }
                else
                {
                    rtpPort = MEDIA_PORT_START;
                    rtcpPort = MEDIA_PORT_START + 1;
                }

                if (MEDIA_PORT_START >= MEDIA_PORT_END)
                {
                    MEDIA_PORT_START = 10000;
                }


                MEDIA_PORT_START += 2;

                mediaPort[0] = rtpPort;
                mediaPort[1] = rtcpPort;
            }

            return mediaPort;
        }

        #endregion
    }
}