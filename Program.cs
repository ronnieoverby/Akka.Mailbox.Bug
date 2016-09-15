using Akka.Actor;
using Akka.Configuration;
using Akka.Dispatch;
using CoreTechs.Common;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using static Akka.Mailbox.Bug.PubSub.Messages;

namespace Akka.Mailbox.Bug
{
    class Assertions
    {
        public bool MailboxCreated { get; set; }
        public bool PriorityGenerated { get; set; }
        public bool MessagesReceived { get; set; }

        public override string ToString() =>
            new { MailboxCreated, PriorityGenerated, MessagesReceived }.ToString();

        public static readonly Assertions Instance = new Assertions();
    }

    class Program
    {

        static void Main(string[] args)
        {
            using (var actSys = ActorSystem.Create("ChuckNorris"))
            {
                var pubsub = actSys.ActorOf(Props.Create(() => new PubSub()).WithMailbox("pubsub-mailbox"));
                var sub = actSys.ActorOf(Props.Create(() => new Sub(pubsub)));
                var pub = actSys.ActorOf(Props.Create(() => new Pub(pubsub)));

                pub.Tell("hey bo");

                actSys.Terminate().Wait();
            }

            Assert.True(Assertions.Instance.MessagesReceived);
            Assert.True(Assertions.Instance.MailboxCreated);
            Assert.True(Assertions.Instance.PriorityGenerated);

            Console.ReadLine();
        }
    }

    public class Pub : ReceiveActor
    {
        public Pub(IActorRef pubsub)
        {
            ReceiveAny(x => pubsub.Tell(x));
        }
    }

    public class Sub : ReceiveActor
    {
        public Sub(IActorRef pubsub)
        {
            pubsub.Tell(Subscribe.To<object>());
            ReceiveAny(o => Console.WriteLine(o));
        }
    }


    public class PubSub : ReceiveActor
    {
        readonly Dictionary<Type, HashSet<IActorRef>> _subscribers = new Dictionary<Type, HashSet<IActorRef>>();

        public PubSub()
        {
            Action gotMsg = () => Assertions.Instance.MessagesReceived = true;

            Receive<Subscribe>(s => { gotMsg(); HandleSubscribe(s); });
            Receive<Unsubscribe>(s => { gotMsg(); HandleUnsubscribe(s); });
            ReceiveAny(obj => { gotMsg(); HandlePublish(obj); });
        }

        private void HandleSubscribe(Subscribe s)
        {
            HashSet<IActorRef> subscribers;
            if (!_subscribers.TryGetValue(s.MessageType, out subscribers))
                subscribers = _subscribers[s.MessageType] = new HashSet<IActorRef>();

            subscribers.Add(Sender);
        }

        private void HandleUnsubscribe(Unsubscribe s)
        {
            HashSet<IActorRef> subscribers;
            if (_subscribers.TryGetValue(s.MessageType, out subscribers))
            {
                subscribers.Remove(Sender);

                if (subscribers.Count == 0)
                    _subscribers.Remove(s.MessageType);
            }
        }

        private void HandlePublish(object message)
        {
            if (message == null)
                return;

            var keyTypes = GetKeyTypes(message.GetType());

            HashSet<IActorRef> subscribers = null;
            var subcribers = (from type in keyTypes
                              where _subscribers.TryGetValue(type, out subscribers)
                              from sub in subscribers
                              select sub).Distinct();

            foreach (var sub in subcribers)
                sub.Tell(message);
        }

        private Type[] GetKeyTypes(Type type) =>
            Memoizer.Instance.Get(type, () => EnumerateKeyTypes(type).ToArray())
            // fresh array so cached instance can't be tampered with
            .ToArray();

        private IEnumerable<Type> EnumerateKeyTypes(Type type)
        {
            yield return type;

            var baseType = type.BaseType;
            while (baseType != null)
            {
                yield return baseType;
                baseType = baseType.BaseType;
            }

            foreach (var ifType in type.GetInterfaces())
                yield return ifType;
        }

        public static class Messages
        {
            public class Subscribe
            {
                public Type MessageType { get; }

                public Subscribe(Type msgType)
                {
                    MessageType = msgType;
                }

                public static Subscribe To<T>() => new Subscribe(typeof(T));
            }

            public class Unsubscribe
            {
                public Type MessageType { get; }

                public Unsubscribe(Type msgType)
                {
                    MessageType = msgType;
                }
                public static Unsubscribe To<T>() => new Unsubscribe(typeof(T));
            }

        }
    }
    
    public class PubSubMailbox : UnboundedPriorityMailbox
    {
        public PubSubMailbox(Settings settings, Config config) : base(settings, config)
        {
            Assertions.Instance.MailboxCreated = true;
        }

        protected override int PriorityGenerator(object message)
        {
            Assertions.Instance.PriorityGenerated = true;

            if (message is Unsubscribe || message is Subscribe)
                return 0;

            return 1;
        }
    }
}
