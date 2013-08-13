﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Threading;
using Castle.Core.Logging;
using Inceptum.Core.Utils;
using Inceptum.Messaging.Contract;
using Inceptum.Messaging.InMemory;
using Inceptum.Messaging.Serialization;
using Inceptum.Messaging.Transports;

namespace Inceptum.Messaging
{
    public class MessagingEngine : IMessagingEngine
    {
        private const int DEFAULT_UNACK_DELAY = 60000;
        internal const int MESSAGE_DEFAULT_LIFESPAN = 0; // forever // 1800000; // milliseconds (30 minutes)
        private readonly ManualResetEvent m_Disposing = new ManualResetEvent(false);
        private readonly CountingTracker m_RequestsTracker = new CountingTracker();
        private readonly ISerializationManager m_SerializationManager;
        private readonly List<IDisposable> m_MessagingHandles = new List<IDisposable>();
        private readonly TransportManager m_TransportManager;

        //TODO: verify logging. I've added param but never tested
        private ILogger m_Logger = NullLogger.Instance;
        readonly ConcurrentDictionary<Type, string> m_MessageTypeMapping = new ConcurrentDictionary<Type, string>();
        private readonly SchedulingBackgroundWorker m_RequestTimeoutManager;
        private readonly SchedulingBackgroundWorker m_DeferredAcknowledgementManager;
        readonly Dictionary<RequestHandle, Action<Exception>> m_ActualRequests = new Dictionary<RequestHandle, Action<Exception>>();
        readonly List<Tuple<DateTime, Action>> m_DeferredAcknowledgements = new List<Tuple<DateTime, Action>>();

        /// <summary>
        /// ctor for tests
        /// </summary>
        /// <param name="transportManager"></param>
        internal MessagingEngine(TransportManager transportManager)
        {
            if (transportManager == null) throw new ArgumentNullException("transportManager");
            m_TransportManager = transportManager;
            m_SerializationManager = new SerializationManager();
            m_RequestTimeoutManager = new SchedulingBackgroundWorker("RequestTimeoutManager", () => stopTimeoutedRequests());
            m_DeferredAcknowledgementManager = new SchedulingBackgroundWorker("DeferredAcknowledgementManager", () => processDefferredAcknowledgements());
            createMessagingHandle(() => stopTimeoutedRequests(true));

        }





        public MessagingEngine(ITransportResolver transportResolver, params ITransportFactory[] transportFactories)
            : this(new TransportManager(transportResolver, transportFactories))
        {
        } 
        
        public MessagingEngine(ITransportResolver transportResolver)
            : this(new TransportManager(transportResolver))
        {
        }


        public ISerializationManager SerializationManager
        {
            get { return m_SerializationManager; }
        }

        public ILogger Logger
        {
            get { return m_Logger; }
            set { m_Logger = value; }
        }

        #region IMessagingEngine Members

        public IDisposable SubscribeOnTransportEvents(TrasnportEventHandler handler)
        {
            TrasnportEventHandler safeHandler = (transportId, @event) =>
                                                    {
                                                        try
                                                        {
                                                            handler(transportId, @event);
                                                        }
                                                        catch (Exception ex)
                                                        {
                                                            Logger.WarnFormat(ex, "transport events handler failed");
                                                        }
                                                    };
            m_TransportManager.TransportEvents += safeHandler;
            return Disposable.Create(() => m_TransportManager.TransportEvents -= safeHandler);
        }

        public void Send<TMessage>(TMessage message, Endpoint endpoint)
        {
            Send(message, endpoint, MESSAGE_DEFAULT_LIFESPAN);
        }

        public void Send<TMessage>(TMessage message, Endpoint endpoint, int ttl)
        {
            var serializedMessage = serializeMessage(endpoint.SerializationFormat, message);
            send(serializedMessage,endpoint,ttl);
        }

 

        public void Send(object message, Endpoint endpoint)
        {
            var type = getMessageType(message.GetType());
            var bytes = m_SerializationManager.SerializeObject(endpoint.SerializationFormat, message);
            var serializedMessage = new BinaryMessage { Bytes = bytes, Type = type };
            send(serializedMessage,endpoint,MESSAGE_DEFAULT_LIFESPAN);
        }
        
        private void send(BinaryMessage message, Endpoint endpoint, int ttl)
        {
            if (endpoint.Destination == null) throw new ArgumentException("Destination can not be null");
            if (m_Disposing.WaitOne(0))
                throw new InvalidOperationException("Engine is disposing");

            using (m_RequestsTracker.Track())
            {
                try
                {
                    var processingGroup = m_TransportManager.GetProcessingGroup(endpoint.TransportId,endpoint.Destination);
                    //m_SerializationManager.SerializeObject(endpoint.SerializationFormat, message)
                //    var serializedMessage = serialize(endpoint.SerializationFormat, message);
                    processingGroup.Send(endpoint.Destination, message, ttl);
                }
                catch (Exception e)
                {
					Logger.ErrorFormat(e, "Failed to send message. Transport: {0}, Queue: {1}", endpoint.TransportId, endpoint.Destination);
                    throw;
                }
            }
        }


		public IDisposable Subscribe<TMessage>(Endpoint endpoint, Action<TMessage> callback)
		{
            return Subscribe(endpoint, (TMessage message, AcknowledgeDelegate acknowledge) =>
		        {
		            callback(message);
		            acknowledge(0,true);
		        });
		}
        public IDisposable Subscribe<TMessage>(Endpoint endpoint, CallbackDelegate<TMessage> callback)
        {
			if (endpoint.Destination == null) throw new ArgumentException("Destination can not be null");
            if (m_Disposing.WaitOne(0))
                throw new InvalidOperationException("Engine is disposing");

            using (m_RequestsTracker.Track())
            {
                try
                {
                    return subscribe(endpoint, (m, ack) => processMessage(m, typeof(TMessage),  message  => callback((TMessage)message, ack), ack, endpoint), endpoint.SharedDestination ? getMessageType(typeof(TMessage)) : null);
                }
                catch (Exception e)
                {
                    Logger.ErrorFormat(e, "Failed to subscribe. Transport: {0}, Queue: {1}",  endpoint.TransportId, endpoint.Destination);
                    throw;
                }
            }
        }

        public IDisposable Subscribe(Endpoint endpoint, Action<object> callback, Action<string> unknownTypeCallback,
                                     params Type[] knownTypes)
        {
            return Subscribe(endpoint,
                             (message, acknowledge) =>
                                 {
                                     callback(message);
                                     acknowledge(0, true);
                                 },
                             (type, acknowledge) =>
                                 {
                                     unknownTypeCallback(type);
                                     acknowledge(0, true);
                                 },
                             knownTypes);
        }

        public IDisposable Subscribe(Endpoint endpoint, CallbackDelegate<object> callback, Action<string, AcknowledgeDelegate> unknownTypeCallback, params Type[] knownTypes)
        {
            if (endpoint.Destination == null) throw new ArgumentException("Destination can not be null");
            if (m_Disposing.WaitOne(0))
                throw new InvalidOperationException("Engine is disposing");

            using (m_RequestsTracker.Track())
            {
                try
                {
                    var dictionary = knownTypes.ToDictionary(getMessageType);

                    return subscribe(endpoint, (m,ack) =>
                        {
                            Type messageType;
                            if (!dictionary.TryGetValue(m.Type, out messageType))
                            {
                                try
                                {
                                    unknownTypeCallback(m.Type, ack);
                                }
                                catch (Exception e)
                                {
                                    Logger.ErrorFormat(e, "Failed to handle message of unknown type. Transport: {0}, Queue {1}, Message Type: {2}",
                                   endpoint.TransportId, endpoint.Destination, m.Type);
                                }
                                return;
                            }
                            processMessage(m, messageType, message => callback(message, ack), ack, endpoint);
                        }, null);
                }
                catch (Exception e)
                {
                    Logger.ErrorFormat(e, "Failed to subscribe. Transport: {0}, Queue: {1}", endpoint.TransportId, endpoint.Destination);
                    throw;
                }
            }
        }



        //NOTE: send via topic waits only first response.
        public TResponse SendRequest<TRequest, TResponse>(TRequest request, Endpoint endpoint, long timeout)
        {
            if (m_Disposing.WaitOne(0))
                throw new InvalidOperationException("Engine is disposing");

            using (m_RequestsTracker.Track())
            {
                var responseRecieved = new ManualResetEvent(false);
                TResponse response = default(TResponse);
                Exception exception = null;

				using (SendRequestAsync<TRequest, TResponse>(request, endpoint,
                                                             r =>
                                                                 {
                                                                     response = r;
                                                                     responseRecieved.Set();
                                                                 },
                                                             ex =>
                                                                 {
                                                                     exception = ex;
                                                                     responseRecieved.Set();
                                                                 },timeout))
                {
                    int waitResult = WaitHandle.WaitAny(new WaitHandle[] {m_Disposing, responseRecieved});
                    switch (waitResult)
                    {
                        case 1:
                            if (exception == null)
                                return response;
                            if(exception is TimeoutException)
                                throw exception;//StackTrace is replaced bat it is ok here.
                            throw new ProcessingException("Failed to process response", exception);
                        case 0:
                            throw new ProcessingException("Request was cancelled due to engine dispose", exception);
 
                        default:
                            throw new InvalidOperationException();
                    }
                }
            }
        }

 

        private void stopTimeoutedRequests(bool stopAll=false)
        {
            lock (m_ActualRequests)
            {
                var timeouted = stopAll
                            ?m_ActualRequests .ToArray()
                            :m_ActualRequests.Where(r => r.Key.DueDate <= DateTime.Now || r.Key.IsComplete).ToArray();

                Array.ForEach(timeouted, r =>
                {
                    r.Key.Dispose();
                    if (!r.Key.IsComplete)
                    {
                        r.Value(new TimeoutException("Request has timed out")); 
                    }
                    m_ActualRequests.Remove(r.Key);
                });
            }
        }


        private void processDefferredAcknowledgements(bool all = false)
        {
            Tuple<DateTime, Action>[] ready;
            lock (m_DeferredAcknowledgements)
            {
                ready = all
                                ? m_DeferredAcknowledgements.ToArray()
                                : m_DeferredAcknowledgements.Where(r => r.Item1 <= DateTime.Now).ToArray();
            }

                Array.ForEach(ready, r => r.Item2());

            lock (m_DeferredAcknowledgements)
            {
                Array.ForEach(ready, r => m_DeferredAcknowledgements.Remove(r));
            }
        }




        public IDisposable SendRequestAsync<TRequest, TResponse>(TRequest request, Endpoint endpoint, Action<TResponse> callback, Action<Exception> onFailure, long timeout)
        {
            if (m_Disposing.WaitOne(0))
                throw new InvalidOperationException("Engine is disposing");

            using (m_RequestsTracker.Track())
            {
                try
                {
                    var processingGroup = m_TransportManager.GetProcessingGroup(endpoint.TransportId,endpoint.Destination);
                    RequestHandle requestHandle = processingGroup.SendRequest(endpoint.Destination, serializeMessage(endpoint.SerializationFormat,request),
                                                                     message =>
                                                                     {
                                                                         try
                                                                         {
                                                                             var responseMessage = m_SerializationManager.Deserialize<TResponse>(endpoint.SerializationFormat, message.Bytes);  
                                                                             callback(responseMessage);
                                                                         }
                                                                         catch (Exception e)
                                                                         {
                                                                             onFailure(e);
                                                                         }
                                                                         finally
                                                                         {
                                                                             m_RequestTimeoutManager.Schedule(1);
                                                                         }
                                                                     });


                    lock (m_ActualRequests)
                    {
                        requestHandle.DueDate = DateTime.Now.AddMilliseconds(timeout);
                        m_ActualRequests.Add(requestHandle, onFailure);
                        m_RequestTimeoutManager.Schedule(timeout);
                    }
                    return requestHandle;

                }
                catch (Exception e)
                {
                    Logger.ErrorFormat(e, "Failed to register handler. Transport: {0}, Destination: {1}",  endpoint.TransportId,
                                       endpoint.Destination);
                    throw;
                }
            }
        }

        public IDisposable RegisterHandler<TRequest, TResponse>(Func<TRequest, TResponse> handler, Endpoint endpoint)
			where TResponse : class
		{
			var handle = new SerialDisposable();
            IDisposable transportWatcher = SubscribeOnTransportEvents((trasnportId, @event) =>
			                                                          	{
			                                                          		if (trasnportId == endpoint.TransportId || @event != TransportEvents.Failure)
			                                                          			return;
			                                                          		registerHandlerWithRetry(handler, endpoint, handle);
			                                                          	});

			registerHandlerWithRetry(handler, endpoint, handle);

			return new CompositeDisposable(transportWatcher, handle);
		}


        public void Dispose()
        {
            m_Disposing.Set();
            m_RequestTimeoutManager.Dispose();
            processDefferredAcknowledgements(true);
            m_DeferredAcknowledgementManager.Dispose();
            m_RequestsTracker.WaitAll();
            lock (m_MessagingHandles)
            {

                while (m_MessagingHandles.Any())
                {
                    m_MessagingHandles.First().Dispose();
                }
            }
            m_TransportManager.Dispose();
        }

        #endregion

        public void registerHandlerWithRetry<TRequest, TResponse>(Func<TRequest, TResponse> handler, Endpoint endpoint, SerialDisposable handle)
            where TResponse : class
        {
            lock (handle)
            {
                try
                {
                    handle.Disposable = registerHandler(handler, endpoint);
                }
                catch
                {
                    Logger.InfoFormat("Scheduling register handler attempt in 1 minute. Transport: {0}, Queue: {1}",
                                       endpoint.TransportId, endpoint.Destination);
                	handle.Disposable = Scheduler.ThreadPool.Schedule(DateTimeOffset.Now.AddMinutes(1),
                	                                                  () =>
                	                                                  	{
                	                                                  		lock (handle)
                	                                                  		{
                	                                                  			registerHandlerWithRetry(handler, endpoint, handle);
                	                                                  		}
                	                                                  	});
                }
            }
        }


		public IDisposable registerHandler<TRequest, TResponse>(Func<TRequest, TResponse> handler, Endpoint endpoint)
            where TResponse : class
        {
            if (m_Disposing.WaitOne(0))
                throw new InvalidOperationException("Engine is disposing");

            using (m_RequestsTracker.Track())
            {
                try
                {
                    var processingGroup = m_TransportManager.GetProcessingGroup( endpoint.TransportId, endpoint.Destination);
                	var subscription = processingGroup.RegisterHandler(endpoint.Destination,
                	                                                     requestMessage =>
                	                                                     	{
                                                                                var message = m_SerializationManager.Deserialize<TRequest>(endpoint.SerializationFormat, requestMessage.Bytes); 
                	                                                     		TResponse response = handler(message);
                	                                                     		return serializeMessage(endpoint.SerializationFormat,response);
                	                                                     	},
                	                                                     endpoint.SharedDestination
                	                                                     	? getMessageType(typeof (TRequest))
                	                                                     	: null
                		);
                	var messagingHandle = createMessagingHandle(() =>
                	                                            	{
                	                                            		try
                	                                            		{
                	                                            			subscription.Dispose();
                	                                            			Disposable.Create(() => Logger.InfoFormat("Handler was unregistered. Transport: {0}, Queue: {1}", endpoint.TransportId, endpoint.Destination));
                	                                            		}
                	                                            		catch (Exception e)
                	                                            		{
                	                                            			Logger.WarnFormat(e, "Failed to unregister handler. Transport: {0}, Queue: {1}", endpoint.TransportId, endpoint.Destination);
                	                                            		}
                	                                            	});

                    Logger.InfoFormat("Handler was successfully registered. Transport: {0}, Queue: {1}",  endpoint.TransportId, endpoint.Destination);
                    return messagingHandle;
                }
                catch (Exception e)
                {
                    Logger.ErrorFormat(e, "Failed to register handler. Transport: {0}, Queue: {1}",  endpoint.TransportId, endpoint.Destination);
                    throw;
                }
            }
        }


        private BinaryMessage serializeMessage<TMessage>(string format,TMessage message)
        {
            var type = getMessageType(typeof(TMessage));
            var bytes = m_SerializationManager.Serialize(format,message);
            return new BinaryMessage{Bytes=bytes,Type=type};
        }
    

        private string getMessageType(Type type)
        {
        	return m_MessageTypeMapping.GetOrAdd(type, clrType =>
        	                                           	{
                                                            //TODO: type should be determined by serializer
        	                                           		var typeName = clrType.GetCustomAttributes(false)
        	                                           			.Select(a => a as ProtoBuf.ProtoContractAttribute)
        	                                           			.Where(a => a != null)
        	                                           			.Select(a => a.Name)
        	                                           			.FirstOrDefault();
        	                                           		return typeName ?? clrType.Name;
        	                                           	});
        }
         

        private IDisposable subscribe(Endpoint endpoint,CallbackDelegate<BinaryMessage> callback, string messageType)
        {
            var processingGroup = m_TransportManager.GetProcessingGroup(endpoint.TransportId , endpoint.Destination);
            IDisposable subscription = processingGroup.Subscribe(endpoint.Destination, (message, ack) => callback(message,createDeferredAcknowledge(ack)), messageType);
            return createMessagingHandle(subscription.Dispose);
        }

        private AcknowledgeDelegate createDeferredAcknowledge(Action<bool> ack)
        {
            return (l, b) =>
                {
                    if (l == 0)
                    {
                        ack(b);
                        return;
                    }

                    lock (m_DeferredAcknowledgements)
                    {
                        m_DeferredAcknowledgements.Add(Tuple.Create<DateTime,Action>(DateTime.Now.AddMilliseconds(l),() => ack(b)));
                        m_DeferredAcknowledgementManager.Schedule(l);
                    }
                };
        }


        private IDisposable createMessagingHandle(Action destroy)
        {
            IDisposable handle = null;

            handle = Disposable.Create(() =>
                                           {
                                               destroy();
                                               lock (m_MessagingHandles)
                                               {
// ReSharper disable AccessToModifiedClosure
                                                   m_MessagingHandles.Remove(handle);
// ReSharper restore AccessToModifiedClosure
                                               }
                                           });
            lock (m_MessagingHandles)
            {
                m_MessagingHandles.Add(handle);
            }
            return handle;
        }



        private void processMessage(BinaryMessage binaryMessage,Type type, Action<object> callback, AcknowledgeDelegate ack, Endpoint endpoint)
        {
            object message = null;
            try
            {
                message = m_SerializationManager.Deserialize(endpoint.SerializationFormat, binaryMessage.Bytes, type);
            }
            catch (Exception e)
            {
                Logger.ErrorFormat(e, "Failed to deserialize message. Transport: {0}, Destination: {1}, Message Type: {2}",
                                   endpoint.TransportId, endpoint.Destination, type.Name);
                //TODO: need to unack without requeue
                ack(DEFAULT_UNACK_DELAY, false);
            }

            try
            {
                callback(message);
            }
            catch (Exception e)
            {
                Logger.ErrorFormat(e, "Failed to handle message. Transport: {0}, Destination: {1}, Message Type: {2}",
                                   endpoint.TransportId, endpoint.Destination, type.Name);
                ack(DEFAULT_UNACK_DELAY, false);
            }
        }
    }
}