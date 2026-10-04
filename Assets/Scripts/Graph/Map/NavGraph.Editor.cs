using System.Collections.Generic;
using UnityEngine;

namespace Assets.Scripts.Graph
{
    // Menus de contexto de autoria (só editor) e o auto-coletar ligado à hierarquia.
    public partial class NavGraph
    {
#if UNITY_EDITOR
        /// <summary>
        /// Refaz o bake e loga cada sala (nós, portas) e cada porta problemática; depois o gizmo mostra "S#"
        /// em cada sala (até recarregar scripts).
        /// </summary>
        [ContextMenu("Relatório de salas e portas")]
        internal void LogRoomReport()
        {
            _isBaked = false;
            EnsureBaked();

            var builder = new System.Text.StringBuilder();
            builder.Append($"{name}: {RoomCount} sala(s)\n");
            for (int r = 0; r < RoomCount; r++)
            {
                builder.Append($"  S{r}: {_roomNodes[r].Length} nó(s), {_roomDoors[r].Length} porta(s) [");
                for (int k = 0; k < _roomDoors[r].Length; k++)
                {
                    if (k > 0)
                        builder.Append(", ");
                    builder.Append(_nodes[_roomDoors[r][k]].name);
                }

                builder.Append("]\n");
            }

            Debug.Log(builder.ToString(), this);
        }

        [ContextMenu("Coletar nós filhos")]
        internal void CollectChildNodes()
        {
            UnityEditor.Undo.RecordObject(this, "Coletar nós");
            _nodes.Clear();
            _nodes.AddRange(GetComponentsInChildren<NavNode>(includeInactive: true));
            _nodeSetCount = -1;
            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"{name}: {_nodes.Count} nós coletados.", this);
        }

        // Só para o ladrilhamento do NavGraphPlacer, que grava o Undo antes.
        internal void SetShape(NodeShape shape) => _nodeShape = shape;

        /// <summary>
        /// Traz para este grafo todo NavNode da MESMA arena sem NavGraph acima e recoleta a lista (com Undo; a
        /// versão automática não grava).
        /// </summary>
        [ContextMenu("Adotar nós soltos da arena e coletar")]
        private void AdoptAndCollect()
        {
            int adopted = AdoptStrayNodes(recordUndo: true);
            CollectChildNodes();
            if (adopted > 0)
                Debug.Log($"{name}: {adopted} nó(s) solto(s) da arena agora são filhos deste grafo.", this);
        }

        /// <summary>
        /// Chamado pela hierarquia do editor (NavGraphAutoCollect) com _autoCollectNodes ligado: adota nós soltos e
        /// recoleta SE a lista mudou (evita marcar as arenas como modificadas a cada clique).
        /// </summary>
        internal void AutoCollect()
        {
            if (!_autoCollectNodes || Application.isPlaying)
                return;

            AdoptStrayNodes(recordUndo: false);

            NavNode[] children = GetComponentsInChildren<NavNode>(includeInactive: true);
            _nodes.RemoveAll(node => node == null);
            if (children.Length == _nodes.Count && new HashSet<NavNode>(children).SetEquals(_nodes))
                return;

            // A ordem dos filhos manda na ordem da lista, a mesma do "Coletar nós filhos".
            _nodes.Clear();
            _nodes.AddRange(children);
            _nodeSetCount = -1;
            UnityEditor.EditorUtility.SetDirty(this);
            if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(this))
                UnityEditor.PrefabUtility.RecordPrefabInstancePropertyModifications(this);
        }

        // Nós soltos = na mesma arena e sem NavGraph acima; nó de outro grafo nunca é roubado. Nó de instância de
        // prefab não pode trocar de pai pela cena: fica onde está, desenhando a própria área, até abrir o prefab.
        private int AdoptStrayNodes(bool recordUndo)
        {
            GraphArenaController arena = GetComponentInParent<GraphArenaController>(true);
            Transform root = arena != null ? arena.transform : transform.root;
            int adopted = 0;

            foreach (NavNode node in root.GetComponentsInChildren<NavNode>(includeInactive: true))
            {
                if (node.GetComponentInParent<NavGraph>(true) != null)
                    continue;

                GameObject go = node.gameObject;
                if (UnityEditor.PrefabUtility.IsPartOfPrefabInstance(go) && !UnityEditor.PrefabUtility.IsAddedGameObjectOverride(go))
                    continue;

                if (recordUndo)
                    UnityEditor.Undo.SetTransformParent(node.transform, transform, "Adotar nós soltos");
                else
                    node.transform.SetParent(transform, worldPositionStays: true);

                adopted++;
            }

            return adopted;
        }

        /// <summary>
        /// Liga todo par de nós a até <see cref="_autoLinkMaxDistance"/> com linha livre. Ponto de partida, não
        /// substituto da autoria: gera ligações redundantes em sala aberta. Só adiciona, nunca remove.
        /// </summary>
        [ContextMenu("Auto-ligar por linha de visão")]
        private void AutoLinkByLineOfSight()
        {
            _nodes.RemoveAll(node => node == null);

            int created = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                for (int j = i + 1; j < _nodes.Count; j++)
                {
                    NavNode a = _nodes[i];
                    NavNode b = _nodes[j];

                    if (Vector3.Distance(a.Position, b.Position) > _autoLinkMaxDistance)
                        continue;

                    if (!IsSegmentClear(a.Position, b.Position))
                        continue;

                    if (a.IsNeighbor(b) || b.IsNeighbor(a))
                        continue;

                    UnityEditor.Undo.RecordObject(a, "Auto-ligar nós");
                    a.EditableNeighbors.Add(b);
                    UnityEditor.EditorUtility.SetDirty(a);
                    created++;
                }
            }

            Debug.Log($"{name}: {created} ligações criadas.", this);
        }

        [ContextMenu("Validar ligações")]
        private void ValidateLinks()
        {
            _nodes.RemoveAll(node => node == null);
            for (int i = 0; i < _nodes.Count; i++)
                _nodes[i].AssignIndex(i);

            int blocked = 0;
            foreach (NavNode node in _nodes)
            {
                foreach (NavNode neighbor in node.Neighbors)
                {
                    if (neighbor == null)
                    {
                        Debug.LogWarning($"{node.name}: ligação vazia na lista de vizinhos.", node);
                        continue;
                    }

                    if (!IsSegmentClear(node.Position, neighbor.Position))
                    {
                        Debug.LogWarning($"{node.name} -> {neighbor.name}: a ligação atravessa parede.", node);
                        blocked++;
                    }
                }
            }

            Debug.Log($"{name}: validação concluída — {blocked} ligação(ões) bloqueada(s) em {_nodes.Count} nós.", this);
        }
#endif
    }

#if UNITY_EDITOR
    /// <summary>
    /// Liga o auto-coletar do <see cref="NavGraph"/> à hierarquia do editor. Gancho estático porque fora do Play o
    /// Unity não chama OnEnable de MonoBehaviour comum; adiado (delayCall) para não reentrar no evento de hierarquia.
    /// Olha só o estágio aberto (o prefab no Prefab Mode, senão as cenas).
    /// </summary>
    [UnityEditor.InitializeOnLoad]
    internal static class NavGraphAutoCollect
    {
        private static bool _queued;

        static NavGraphAutoCollect()
        {
            UnityEditor.EditorApplication.hierarchyChanged += OnHierarchyChanged;
        }

        private static void OnHierarchyChanged()
        {
            if (_queued || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            _queued = true;
            UnityEditor.EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            _queued = false;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            UnityEditor.SceneManagement.StageHandle stage = UnityEditor.SceneManagement.StageUtility.GetCurrentStageHandle();
            foreach (NavGraph graph in stage.FindComponentsOfType<NavGraph>())
            {
                if (graph != null)
                    graph.AutoCollect();
            }
        }
    }
#endif
}
