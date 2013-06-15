﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;

namespace Inceptum.Cqrs
{
    public class EventDispatcher
    {
        readonly Dictionary<Type, List<Action<object, string>>> m_Handlers = new Dictionary<Type, List<Action<object, string>>>();
 

        public void Wire(object o)
        {
            if (o == null) throw new ArgumentNullException("o");
            var handledTypes = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "Handle" && !m.IsGenericMethod && m.GetParameters().Length == 1)
                .Select(m => m.GetParameters().First().ParameterType)
                .Where(p=>!p.IsInterface);

            foreach (var type in handledTypes)
            {
                registerHandler(type,o,false);
            }        
            
            handledTypes = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name == "Handle" && !m.IsGenericMethod && m.GetParameters().Length == 2 && m.GetParameters()[1].Name == "boundedContext" && m.GetParameters()[1].ParameterType==typeof(string))
                .Select(m => m.GetParameters().First().ParameterType)
                .Where(p=>!p.IsInterface);

            foreach (var type in handledTypes)
            {
                registerHandler(type,o,true);
            }
        }

        private void registerHandler(Type parameterType, object o,bool hasBoundedContextParam)
        {
            var @event = Expression.Parameter(typeof(object), "event");
            var boundedContext = Expression.Parameter(typeof(string), "boundedContext");
            Expression[] parameters =hasBoundedContextParam
                ? new Expression[] { Expression.Convert(@event, parameterType) ,boundedContext}
                : new Expression[] { Expression.Convert(@event, parameterType) };
            var call = Expression.Call(Expression.Constant(o), "Handle", null, parameters);
            var lambda = (Expression<Action<object, string>>)Expression.Lambda(call, @event, boundedContext);

            List<Action<object, string>> list;
            if (!m_Handlers.TryGetValue(parameterType, out list))
            {
                list = new List<Action<object, string>>();
                m_Handlers.Add(parameterType,list);
            }
            list.Add(lambda.Compile());
            
        }
      
        public void Dispacth(object @event, string boundedContext)
        {
            List<Action<object,string>> list;
            if (!m_Handlers.TryGetValue(@event.GetType(), out list))
                return;
            foreach (var handler in list)
            {
                handler(@event, boundedContext);
                //TODO: event handling
            }
        }
    }
}