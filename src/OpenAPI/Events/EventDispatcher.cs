﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenAPI.Utils;

namespace OpenAPI.Events
{
	public class EventDispatcher
	{
		private static readonly ILog Log = LogManager.GetLogger(typeof(EventDispatcher));

		private static readonly ThreadSafeList<Type> EventTypes = new ThreadSafeList<Type>
		{
			AppDomain.CurrentDomain.GetAssemblies()
				.SelectMany(s => s.GetTypes())
				.Where(p =>
				{
					if (p.IsClass && !p.IsAbstract && typeof(Event).IsAssignableFrom(p))
					{
						Log.Info($"Registered event type \"{p.Name}\"");
						return true;
					}

					return false;
				}).ToArray()
		};

		public void RegisterEventType<TEvent>() where TEvent : Event
		{
			Type t = typeof(TEvent);
			if (RegisteredEvents.ContainsKey(t) || !EventTypes.TryAdd(t))
			{
				throw new DuplicateTypeException();
			}
			else
			{
				RegisteredEvents.Add(t, new EventDispatcherValues());
				Log.Info($"Registered event type \"{t.Name}\"");
			}
		}

		private Dictionary<Type, EventDispatcherValues> RegisteredEvents { get; }
		protected OpenAPI Api { get; }
		private EventDispatcher[] ExtraDispatchers { get; }
		public EventDispatcher(OpenAPI openApi, params EventDispatcher[] dispatchers)
		{
			Api = openApi;
			ExtraDispatchers = dispatchers.Where(x => x != this).ToArray();

			RegisteredEvents = new Dictionary<Type, EventDispatcherValues>();
			foreach (var eventType in EventTypes)
			{
				RegisteredEvents.Add(eventType, new EventDispatcherValues());
			}
			//Log.Info($"Registered {RegisteredEvents.Count} event types!");
		}

		public void RegisterEvents(IEventHandler obj)
		{
			int count = 0;

			var type = typeof(Event);
			Type objType = obj.GetType();
			foreach (var method in objType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
			{
				EventHandlerAttribute attribute = method.GetCustomAttribute<EventHandlerAttribute>(false);
				if (attribute == null) continue;

				var parameters = method.GetParameters();
				if (parameters.Length != 1 || !type.IsAssignableFrom(parameters[0].ParameterType)) continue;

				var paramType = parameters[0].ParameterType;

			    EventDispatcherValues e = null;
                if (!RegisteredEvents.TryGetValue(paramType, out e))
			    {
			        if (EventTypes.TryAdd(paramType))
			        {
			            e = new EventDispatcherValues();
                        RegisteredEvents.Add(paramType, e);
			        }
			    }

				if (!e.RegisterEventHandler(attribute, obj, method))
				{
					Log.Warn($"Duplicate found for class \"{obj.GetType()}\" of type \"{paramType}\"");
				}
				else
				{
					count++;
				}
			}

			Log.Info($"Registered {count} event handlers for \"{obj}\"");
		}

		public void UnregisterEvents(IEventHandler obj)
		{
			foreach (var kv in RegisteredEvents.ToArray())
			{
				kv.Value.Clear(obj);
			}
		}

		private void DispatchPrivate(Event e)
		{
			try
			{
				Type type = e.GetType();
				if (RegisteredEvents.TryGetValue(type, out EventDispatcherValues v))
				{
					v.Dispatch(e);
				}
				else
				{
					Log.Warn($"Unknown event type found! \"{type}\"");
				}
			}
			catch (Exception ex)
			{
				Log.Error("Error while dispatching event!", ex);
			}
		}

		public void DispatchEvent(Event e)
		{
			DispatchPrivate(e);

			if (!e.IsCancelled)
			{
				foreach (var i in ExtraDispatchers)
				{
					i.DispatchPrivate(e);
					if (e.IsCancelled) break;
				}
			}

			if (Api.ServerInfo != null)
			{
				Interlocked.Increment(ref Api.ServerInfo.EventsDispatchedPerSecond);
			}
		}

		/*public async Task<TEvent> DispatchEventAsync<TEvent>(TEvent e) where TEvent : Event
		{
			using (EventMetrics.ReportEvent(e))
			{
				Type type = typeof(TEvent);
				if (RegisteredEvents.ContainsKey(type))
				{
					await RegisteredEvents[type].DispatchAsync(e);
					if (Api.ServerInfo != null)
					{
						Interlocked.Increment(ref Api.ServerInfo.EventsDispatchedPerSecond);
					}
				}
				else
				{
					Log.Warn($"Unknown event type found! \"{type}\"");
				}

				foreach (var i in ExtraDispatchers)
				{
					await i.DispatchEventAsync(e);
				}

				return e;
			}
		}*/

		private class EventDispatcherValues
		{
		//	private ConcurrentDictionary<IEventHandler, MethodInfo> EventHandlers { get; }
            private Dictionary<EventPriority, List<Item>> Items { get; }
			//private SortedSet<Item> Items { get; set; }
			public EventDispatcherValues()
			{
                Items = new Dictionary<EventPriority, List<Item>>();
			    foreach (var prio in Enum.GetValues(typeof(EventPriority)))
			    {
                    Items.Add((EventPriority) prio, new System.Collections.Generic.List<Item>());
			    }
				//Items = new SortedSet<Item>();
			//	EventHandlers = new ConcurrentDictionary<IEventHandler, MethodInfo>();
			}

			public bool RegisterEventHandler(EventHandlerAttribute attribute, IEventHandler parent, MethodInfo method)
			{
			    Items[attribute.Priority].Add(new Item(attribute, parent, method));
			    return true;
				//return Items.Add(new Item(attribute, parent, method));
				/*if (!EventHandlers.TryAdd(parent, method))
				{
					return true;
				}
				return false;*/
			}

			public void Clear(IEventHandler parent)
			{
			    foreach (var priorityList in Items.ToArray())
			    {
			        try
			        {
			            var copy = priorityList.Value.ToArray();
			            foreach (var item in copy)
			            {
			                try
			                {
			                    if (item.Parent == parent)
			                    {
			                        if (priorityList.Value.Count > 0)
			                        {
			                            priorityList.Value.Remove(item);
			                        }
			                    }
			                }
			                catch (Exception x)
			                {

			                }
			            }
			        }
			        catch (Exception ex)
			        {

			        }
			    }
				//Items.RemoveWhere(x => x.Parent == parent);
				//MethodInfo method;
				//EventHandlers.TryRemove(parent, out method);
			}

			public void Dispatch(Event e)
			{
				object[] args = {
					e
				};

			    foreach (var priority in Items)
			    {
			        Parallel.ForEach(priority.Value.ToArray(), pair =>
			        {
			            if (e.IsCancelled &&
			                pair.Attribute.IgnoreCanceled)
			                return;

			            pair.Method.Invoke(pair.Parent, args);
			        });
                }
			}

			/*public async Task DispatchAsync(Event e)
			{
				object[] args = {
					e
				};

				await Task.WhenAll(EventHandlers.Select(item => Task.Run(() => item.Value.Invoke(item.Key, args))));
			}*/

			private struct Item : IComparable<Item>
			{
				//public EventPriority Priority;
				public EventHandlerAttribute Attribute;
				public IEventHandler Parent;
				public MethodInfo Method;
				public Item(EventHandlerAttribute attribute, IEventHandler parent, MethodInfo method)
				{
					Attribute = attribute;
					Parent = parent;
					Method = method;
				}

				public int CompareTo(Item other)
				{
					int result = Attribute.Priority.CompareTo(other.Attribute.Priority);

					if (result == 0)
						result = Parent.GetHashCode().CompareTo(other.Parent.GetHashCode());
					
						return result;
				}
			}

			//private class ItemCompare
		}
	}
}
