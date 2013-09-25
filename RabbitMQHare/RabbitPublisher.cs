﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using RabbitMQ.Client;
using System.Threading.Tasks;

namespace RabbitMQHare
{
    /// <summary>
    /// Settings used to construct a publisher. If you don't use HarePublisherSettings.DefaultSettings you have to fill all parameters.
    /// </summary>
    public struct HarePublisherSettings : IHareSettings
    {
        /// <summary>
        /// Maximum number of message waiting to be sent. Above this limit, new messages won't be added. DefaultSettings sets it to 10000
        /// </summary>
        public int MaxMessageWaitingToBeSent { get; set; }

        /// <summary>
        /// You can provide a way to modify the IBasicProperties of all messages. DefaultSettings sets it to  "text/plain" type and transient messages
        /// </summary>
        public Action<IBasicProperties> ConstructProperties { get; set; }

        /// <summary>
        /// RabbitMQ object to specify how to connect to rabbitMQ. DefaultSettings sets it to localhost:5672 on virtual host "/"
        /// </summary>
        public ConnectionFactory ConnectionFactory { get; set; }

        /// <summary>
        /// When connection fails, indicates the maximum numbers of tries before calling the permanent connection failure handler. DefaultSettings sets it to 5. -1 Is infinite
        /// </summary>
        public int MaxConnectionRetry { get; set; }

        /// <summary>
        /// Interval between two retries when connecting to rabbitmq. Default is 5 seconds
        /// </summary>
        public TimeSpan IntervalConnectionTries { get; set; }

        /// <summary>
        /// Prefer to send a message twice than loosing it
        /// Order is not garanteed
        /// </summary>
        public bool RequeueMessageAfterFailure { get; set; }

        public static HarePublisherSettings GetDefaultSettings()
        {
            return new HarePublisherSettings
            {
                ConnectionFactory = new ConnectionFactory { HostName = "localhost", Port = 5672, UserName = "guest", Password = "guest", VirtualHost = "/", RequestedHeartbeat = 60 },
                MaxConnectionRetry = 5,
                IntervalConnectionTries = TimeSpan.FromSeconds(5),
                MaxMessageWaitingToBeSent = 10000,
                ConstructProperties = props =>
                {
                    props.ContentType = "text/plain";
                    props.DeliveryMode = 1;
                },
            };
        }
    }

    /// <summary>
    /// TODO doc 
    /// </summary>
    public class RabbitPublisher : RabbitConnectorCommon
    {
        private IBasicProperties _props;
        private readonly ConcurrentQueue<KeyValuePair<string, byte[]>> _internalQueue;
        private readonly object _lock = new object();

        private readonly RabbitExchange _myExchange;
        private readonly Task _send;
        private readonly CancellationTokenSource _cancellation;
        private readonly object _token = new object();
        private readonly object _tokenBlocking = new object();

        public delegate void NotEnqueued();
        /// <summary>
        /// Called when queue is full and message are not enqueued
        /// </summary>
        public event NotEnqueued NotEnqueuedHandler;

        public delegate void StartHandler(RabbitPublisher pub);
        /// <summary>
        /// Handler called at each start (and restart)
        /// This is different from StartHandler of consumer, so you can modify this at any time
        /// </summary>
        public event StartHandler OnStart;

        private void OnNotEnqueuedHandler()
        {
            var copy = NotEnqueuedHandler; //see http://stackoverflow.com/questions/786383/c-sharp-events-and-thread-safety
            //this behavior allow thread safety and allow to expose event publicly
            if (copy != null)
                try
                {
                    copy();
                }
                catch (Exception e)
                {
                    OnEventHandlerFailure(e);
                }
        }

        private void OnStartHandler()
        {
            var copy = OnStart; //see http://stackoverflow.com/questions/786383/c-sharp-events-and-thread-safety
            //this behavior allow thread safety and allow to expose event publicly
            if (copy != null)
                try
                {
                    copy(this);
                }
                catch (Exception e)
                {
                    OnEventHandlerFailure(e);
                }
        }

        public HarePublisherSettings MySettings { get; private set; }

        public bool Started { get; private set; }

        /// <summary>
        /// Publish to a PUBLIC queue
        /// </summary>
        /// <param name="mySettings">Settings used to construct a publisher</param>
        /// <param name="destinationQueue">public queue you want to connect to</param>
        public RabbitPublisher(HarePublisherSettings mySettings, RabbitQueue destinationQueue)
            : this(mySettings)
        {
            _myExchange = new RabbitExchange(destinationQueue.Name + "-" + "exchange") { AutoDelete = true };
            RedeclareMyTopology = m =>
            {
                _myExchange.Declare(m);
                destinationQueue.Declare(m);
                m.QueueBind(destinationQueue.Name, _myExchange.Name, "toto");
            };
        }

        /// <summary>
        /// Publish to a PUBLIC exchange
        /// </summary>
        /// <param name="mySettings">Settings used to construct a publisher</param>
        /// <param name="exchange">public exchange you want to connect to</param>
        public RabbitPublisher(HarePublisherSettings mySettings, RabbitExchange exchange)
            : this(mySettings)
        {
            _myExchange = exchange;
            RedeclareMyTopology = _myExchange.Declare;
        }

        /// <summary>
        /// Raw constructor, you have to do everything yourself
        /// </summary>
        /// <param name="mySettings"> </param>
        /// <param name="exchange">Exchange you will sent message to. It *won't* be created, you have to create it in the redeclareToplogy parameter</param>
        /// <param name="redeclareTopology">Lambda called everytime we need to recreate the rabbitmq oblects, it should be idempotent</param>
        public RabbitPublisher(HarePublisherSettings mySettings, RabbitExchange exchange, Action<IModel> redeclareTopology)
            : this(mySettings)
        {
            _myExchange = exchange;
            RedeclareMyTopology = redeclareTopology;
        }

        private RabbitPublisher(HarePublisherSettings settings)
            : base(settings)
        {
            _cancellation = new CancellationTokenSource();
            _send = new Task(() => DequeueSend(_cancellation.Token));
            Started = false;
            MySettings = settings;

            _internalQueue = new ConcurrentQueue<KeyValuePair<string, byte[]>>();
        }

        /// <summary>
        /// Start to publish. This method is NOT thread-safe. Advice is to use it once.
        /// </summary>
        public void Start()
        {
            InternalStart();

            if (!Started)
            {
                _send.Start();
                Started = true;
            }
            //no more modifying after this point
            OnStartHandler();
        }

        /// <summary>
        /// Method that will be called everytime there is a network failure.
        /// </summary>
        internal override void SpecificRestart(IModel model)
        {
            _props = model.CreateBasicProperties();
            MySettings.ConstructProperties(_props);
        }

        /// <summary>
        /// The main thread method : dequeue and publish
        /// </summary>
        /// <returns></returns>
        private void DequeueSend(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                KeyValuePair<string, byte[]> res;
                if (_internalQueue.TryPeek(out res))
                {
                    lock (_lock)
                    {
                        while (_internalQueue.TryDequeue(out res))
                        {
                            lock (_tokenBlocking)
                            {
                                Monitor.Pulse(_tokenBlocking);
                            }
                            string routingKey = res.Key;
                            byte[] message = res.Value;
                            try
                            {
                                Model.BasicPublish(_myExchange.Name, routingKey, _props, message);
                            }
                            catch (RabbitMQ.Client.Exceptions.AlreadyClosedException e)
                            {
                                if (MySettings.RequeueMessageAfterFailure)
                                    _internalQueue.Enqueue(res);
                                switch (e.ShutdownReason.ReplyCode)
                                {
                                    case RabbitMQ.Client.Framing.v0_9_1.Constants.AccessRefused:
                                        OnACLFailure(e);
                                        break;
                                    default:
                                        Start();
                                        break;

                                }
                            }
                            catch
                            {
                                if (MySettings.RequeueMessageAfterFailure)
                                    _internalQueue.Enqueue(res);
                                //No need to offer any event handler since reconnection will probably fail at the first time and the standard handlers will be called
                                Start();
                            }
                        }
                    }
                }
                else
                {
                    lock (_token)
                    {
                        Monitor.Wait(_token);
                    }
                }
            }
        }

        /// <summary>
        /// Add message that will be sent asynchronously. This method is thread-safe
        /// </summary>
        /// <param name="routingKey">routing key used to route the message. If not needed just put "toto"</param>
        /// <param name="message">the message you want to send</param>
        /// <returns>false if the message was droppped instead of added to the queue</returns>
        public bool Publish(string routingKey, byte[] message)
        {
            if (_internalQueue.Count > MySettings.MaxMessageWaitingToBeSent)
            {
                OnNotEnqueuedHandler();
                return false;
            }
            _internalQueue.Enqueue(new KeyValuePair<string, byte[]>(routingKey, message));
            lock (_token)
            {
                Monitor.Pulse(_token);
            }
            return true;
        }

        /// <summary>
        /// Add message that will be sent asynchronously BUT might block if queue is full. This method is thread-safe
        /// </summary>
        /// <param name="routingKey">routing key used to route the message. If not needed just put "toto"</param>
        /// <param name="message">the message you want to send</param>
        public void BlockingPublish(string routingKey, byte[] message)
        {
            if (_internalQueue.Count > MySettings.MaxMessageWaitingToBeSent)
            {
                lock (_tokenBlocking)
                {
                    //TODO : instead of being unblocked by each dequeue, add a low watermark
                    Monitor.Wait(_tokenBlocking);
                }
            }
            _internalQueue.Enqueue(new KeyValuePair<string, byte[]>(routingKey, message));
            lock (_token)
            {
                Monitor.Pulse(_token);
            }
        }

        public override void Dispose()
        {
            _cancellation.Cancel();
            Monitor.TryEnter(_lock, TimeSpan.FromSeconds(30));
            if (Model != null)
            {
                Model.Dispose();
            }
        }


    }
}
