﻿using System;
using System.Collections;
using System.Collections.Generic;
using StrawberryShake;

namespace StrawberryShake.Client.GraphQL
{
    public interface IFriend
    {
        IReadOnlyList<IHasName>? Nodes { get; }
    }
}
