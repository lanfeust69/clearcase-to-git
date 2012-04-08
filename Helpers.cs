using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GitImporter
{
    public static class Helpers
    {
        public static C AddToCollection<K, C, V>(this Dictionary<K, C> dict, K key, V toAdd) where C : ICollection<V>, new()
        {
            C collection;
            if (!dict.TryGetValue(key, out collection))
            {
                collection = new C();
                dict.Add(key, collection);
            }
            collection.Add(toAdd);
            return collection;
        }

        public static bool RemoveFromCollection<K, C, V>(this Dictionary<K, C> dict, K key, V toRemove) where C : ICollection<V>, new()
        {
            C collection;
            if (!dict.TryGetValue(key, out collection))
                return false;
            bool removed = collection.Remove(toRemove);
            if (collection.Count == 0)
                dict.Remove(key);
            return removed;
        }

        public static C AddToCollection<K, C, V>(this List<KeyValuePair<K, C>> dict, K key, V toAdd) where C : ICollection<V>, new()
        {
            C collection = default(C);
            foreach (var pair in dict)
                if (pair.Key.Equals(key))
                {
                    collection = pair.Value;
                    break;
                }

            if (object.Equals(collection, default(C)))
            {
                collection = new C();
                dict.Add(new KeyValuePair<K, C>(key, collection));
            }

            collection.Add(toAdd);
            return collection;
        }

        public static bool RemoveFromCollection<K, C, V>(this List<KeyValuePair<K, C>> dict, K key, V toRemove) where C : ICollection<V>, new()
        {
            C collection = default(C);
            int index;
            for (index = 0; index < dict.Count; index++)
                if (dict[index].Key.Equals(key))
                {
                    collection = dict[index].Value;
                    break;
                }

            if (object.Equals(collection, default(C)))
                return false;
            
            bool removed = collection.Remove(toRemove);
            if (collection.Count == 0)
                dict.RemoveAt(index);
            return removed;
        }
    }
}
