namespace Glacier.Showcase.Tests;

using System;
using System.Collections.Generic;
using Glacier.Serve.Inference.PagedAttention;
using Xunit;

public class PagedAttentionTests
{
    [Fact]
    public void PagedBlockPool_Initialization_SetsCorrectCapacities()
    {
        using var pool = new PagedBlockPool(totalBlocks: 32, layers: 4, headsKv: 2, headDim: 64, blockSize: 16);

        Assert.Equal(32, pool.TotalBlocks);
        Assert.Equal(16, pool.BlockSize);
        Assert.Equal(4, pool.Layers);
        Assert.Equal(2, pool.HeadsKv);
        Assert.Equal(64, pool.HeadDim);
        Assert.Equal(32, pool.FreeBlocksCount);
        Assert.Equal(0, pool.AllocatedBlocksCount);
        Assert.Equal(32 * 16, pool.CapacityTokens);
        Assert.Equal(0, pool.ActiveTokens);
        Assert.True(pool.TotalMemoryBytes > 0);
    }

    [Fact]
    public void PagedBlockPool_AllocateAndFree_MaintainsBlockCounts()
    {
        using var pool = new PagedBlockPool(totalBlocks: 8, layers: 2, headsKv: 2, headDim: 32, blockSize: 16);

        // Allocate 4 blocks
        var allocated = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            bool ok = pool.TryAllocateBlock(out int id);
            Assert.True(ok);
            Assert.InRange(id, 0, 7);
            allocated.Add(id);
        }

        Assert.Equal(4, pool.AllocatedBlocksCount);
        Assert.Equal(4, pool.FreeBlocksCount);

        // Free 2 blocks
        pool.FreeBlock(allocated[0]);
        pool.FreeBlock(allocated[1]);

        Assert.Equal(2, pool.AllocatedBlocksCount);
        Assert.Equal(6, pool.FreeBlocksCount);

        // Free remaining blocks
        pool.FreeBlock(allocated[2]);
        pool.FreeBlock(allocated[3]);

        Assert.Equal(0, pool.AllocatedBlocksCount);
        Assert.Equal(8, pool.FreeBlocksCount);
    }

    [Fact]
    public void PagedBlockPool_Exhaustion_ReturnsFalseWhenFull()
    {
        using var pool = new PagedBlockPool(totalBlocks: 2, layers: 1, headsKv: 1, headDim: 32, blockSize: 16);

        Assert.True(pool.TryAllocateBlock(out int b1));
        Assert.True(pool.TryAllocateBlock(out int b2));
        Assert.False(pool.TryAllocateBlock(out int b3)); // Exhausted

        Assert.Equal(0, pool.FreeBlocksCount);
        Assert.Equal(2, pool.AllocatedBlocksCount);

        // Free one block and ensure we can allocate again
        pool.FreeBlock(b1);
        Assert.True(pool.TryAllocateBlock(out int b4));
        Assert.Equal(b1, b4); // LIFO stack reuse
    }

    [Fact]
    public void PagedBlockPool_FragmentationResilience_ReusesHolesWithoutLeak()
    {
        using var pool = new PagedBlockPool(totalBlocks: 16, layers: 2, headsKv: 2, headDim: 64, blockSize: 16);

        // Allocate all 16 blocks
        var blocks = new List<int>();
        for (int i = 0; i < 16; i++)
        {
            Assert.True(pool.TryAllocateBlock(out int id));
            blocks.Add(id);
        }

        Assert.Equal(0, pool.FreeBlocksCount);

        // Create fragmented pattern: free odd-indexed blocks (8 blocks freed)
        for (int i = 1; i < blocks.Count; i += 2)
        {
            pool.FreeBlock(blocks[i]);
        }

        Assert.Equal(8, pool.FreeBlocksCount);
        Assert.Equal(8, pool.AllocatedBlocksCount);

        // Now allocate 8 blocks again; pool should satisfy all requests
        var reallocated = new List<int>();
        for (int i = 0; i < 8; i++)
        {
            Assert.True(pool.TryAllocateBlock(out int id));
            reallocated.Add(id);
        }

        Assert.Equal(0, pool.FreeBlocksCount);
        Assert.Equal(16, pool.AllocatedBlocksCount);

        // Free all 16 blocks
        for (int i = 0; i < blocks.Count; i += 2) pool.FreeBlock(blocks[i]);
        foreach (int id in reallocated) pool.FreeBlock(id);

        Assert.Equal(16, pool.FreeBlocksCount);
        Assert.Equal(0, pool.AllocatedBlocksCount);
    }

    [Fact]
    public void BlockTable_AppendTokens_SpansMultipleBlocksDynamically()
    {
        using var pool = new PagedBlockPool(totalBlocks: 16, layers: 2, headsKv: 2, headDim: 64, blockSize: 16);
        var table = new BlockTable(pool);

        // Append 40 tokens (needs 3 blocks: 16 + 16 + 8)
        for (int i = 0; i < 40; i++)
        {
            table.AppendToken(out int blockId, out int offset);
            Assert.InRange(blockId, 0, 15);
            Assert.InRange(offset, 0, 15);
        }

        Assert.Equal(40, table.TokenCount);
        Assert.Equal(3, table.BlockCount);
        Assert.Equal(3, pool.AllocatedBlocksCount);

        // Release all blocks
        table.ReleaseAll();
        Assert.Equal(0, table.TokenCount);
        Assert.Equal(0, table.BlockCount);
        Assert.Equal(0, pool.AllocatedBlocksCount);
        Assert.Equal(16, pool.FreeBlocksCount);
    }
}
