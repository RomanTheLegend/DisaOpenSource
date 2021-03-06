﻿using ProtoBuf;
using System;
using System.Collections.Generic;
using System.Text;

namespace Disa.Framework.Stickers
{
    /// <summary>
    /// A value class representing metadata for a <see cref="StickerPack"/>.
    /// </summary>
    [Serializable]
    [ProtoContract]
    [ProtoInclude(100, typeof(FullStickerPack))]
    public class StickerPack
    {
        /// <summary>
        /// Id of the <see cref="StickerPack"/>.
        /// </summary>
        [ProtoMember(151)]
        public string Id { get; set; }

        /// <summary>
        /// The <see cref="Service.Information.ServiceName"/> this <see cref="StickerPack"/> belongs to.
        /// </summary>
        [ProtoMember(152)]
        public string ServiceName { get; set; }

        /// <summary>
        /// Flag indicating if the <see cref="StickerPack"/> is installed
        /// for this user.
        /// </summary>
        [ProtoMember(153)]
        public bool Installed { get; set; }

        /// <summary>
        /// Flag indicating if the <see cref="StickerPack"/> is archived
        /// for this user.
        /// </summary>
        [ProtoMember(154)]
        public bool Archived { get; set; }

        /// <summary>
        /// The <see cref="Sticker"/> to represent this <see cref="StickerPack"/> in UI contexts
        /// such as a list.
        /// </summary>
        [ProtoMember(155)]
        public Sticker FeaturedSticker { get; set; }

        /// <summary>
        /// Title of the <see cref="StickerPack"/>.
        /// </summary>
        [ProtoMember(156)]
        public string Title { get; set; }

        /// <summary>
        /// Count of the <see cref="StickerPack"/>.
        /// </summary>
        [ProtoMember(157)]
        public System.UInt32 Count { get; set; }

        /// <summary>
        /// Set to true if retrieval of stickers for this sticker pack will result in both still
        /// and animated versions of the stickers.
        /// </summary>
        [ProtoMember(158)]
        public bool HasAnimated { get; set; }

        /// <summary>
        /// <see cref="Service"/> specific data as needed by the <see cref="Service"/> for 
        /// accessing and working with the <see cref="StickerPack"/>.
        /// </summary>
        [ProtoMember(159)]
        public byte[] AdditionalData { get; set; }
    }
}
