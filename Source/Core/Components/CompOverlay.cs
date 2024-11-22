using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace GiddyUp
{
	class CompOverlay : ThingComp
	{
		CompProperties_Overlay overlayComp;
		bool valid;
		bool incompatible;
		Pawn pawn;
		Dictionary<Rot4, (GraphicData, Vector3, Vector3, Vector3)> graphicCache = new Dictionary<Rot4, (GraphicData, Vector3, Vector3, Vector3)>();
		
		void CacheGraphicData(Verse.Rot4 rotation)
		{
			var overlay = overlayComp.GetOverlay(rotation);
			if (overlay == null) return;
			
			GraphicData graphicData = ((pawn.gender == Gender.Female) ? overlay.graphicDataFemale : overlay.graphicDataMale) ?? overlay.graphicDataDefault;
			if (graphicData == null) return;

			//support multi texture animals
            if (overlay.allVariants != null)
            {
                string graphicPath = pawn.Drawer?.renderer?.SilhouetteGraphic?.path;
				if (graphicPath.NullOrEmpty()) return;
                string graphicName = graphicPath.Split('/').Last();
                bool found = false;
				foreach (var variant in overlay.allVariants)
                {
                    string variantName = variant.texPath.Split('/').Last().Split(overlay.stringDelimiter.ToCharArray())[0];

                    if (graphicName == variantName)
                    {
                        //set required properties
                        string texPath = variant.texPath;
                        variant.CopyFrom(graphicData);
                        variant.texPath = texPath;
                        graphicData = variant;
						found = true;
						break;
                    }
                }
				if (!found)
				{
					incompatible = true;
					return;
				}
            }

			graphicCache.Add(rotation, (graphicData, overlay.offsetFemale, overlay.offsetMale, overlay.offsetDefault));
			valid = true;
		}
		public void TryCache()
		{
			overlayComp = props as CompProperties_Overlay;
			if (parent is Pawn pawn)
			{
				this.pawn = pawn;
				CacheGraphicData(Rot4.South);
				CacheGraphicData(Rot4.North);
				CacheGraphicData(Rot4.West);
			}
		}
		public override void PostDraw()
		{
			if (valid && pawn.IsMountedAnimal() && graphicCache.TryGetValue(pawn.Rotation == Rot4.East ? Rot4.West : pawn.Rotation, out (GraphicData, Vector3, Vector3, Vector3) cache))
			{	
				Vector3 drawPos = parent.DrawPos;
				Vector3 offset = (pawn.gender == Gender.Female) ? cache.Item2 : cache.Item3;
				if (offset == Vector3.zero) offset = cache.Item4;
				if (pawn.Rotation == Rot4.West) offset.x = -offset.x;
				offset.y += pawn.Rotation == Rot4.South ? 0.08f : 0.04375f; //Tries to render above equipment but below the held weapon

				//Somehow the rotation is flipped, hence the use of GetOpposite.
				cache.Item1.Graphic.Draw(drawPos + offset, parent.Rotation, parent, 0f);
			}
			else if (!valid && !incompatible) TryCache();
		}
	}
}