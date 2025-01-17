﻿/*

Copyright (c) 2019, Gustave Monce - gus33000.me - @gus33000

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

*/
using Img2Ffu.Writer.Helpers;
using Img2Ffu.Writer.Streams;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static Img2Ffu.GPT;

namespace Img2Ffu.Writer
{
    internal class ImageSplitter
    {
        internal static readonly string IS_UNLOCKED_PARTITION_NAME = "IS_UNLOCKED";
        internal static readonly string HACK_PARTITION_NAME = "HACK";
        internal static readonly string BACKUP_BS_NV_PARTITION_NAME = "BACKUP_BS_NV";
        internal static readonly string UEFI_BS_NV_PARTITION_NAME = "UEFI_BS_NV";

        internal static GPT GetGPT(Stream stream, uint BlockSize, uint sectorSize)
        {
            byte[] GPTBuffer = new byte[BlockSize];
            _ = stream.Read(GPTBuffer, 0, (int)BlockSize);

            uint requiredGPTBufferSize = GetGPTSize(GPTBuffer, sectorSize);
            if (BlockSize < requiredGPTBufferSize)
            {
                string errorMessage = $"The Block size is too small to contain the GPT, the GPT is {requiredGPTBufferSize} bytes long, the Block size is {BlockSize} bytes long";
                Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                throw new Exception(errorMessage);
            }

            uint sectorsInABlock = BlockSize / sectorSize;

            GPT GPT = new(GPTBuffer, sectorSize);

            if (BlockSize > requiredGPTBufferSize && GPT.Partitions.OrderBy(x => x.FirstSector).Any(x => x.FirstSector < sectorsInABlock))
            {
                Partition conflictingPartition = GPT.Partitions.OrderBy(x => x.FirstSector).First(x => x.FirstSector < sectorsInABlock);

                string errorMessage = $"The Block size is too big to contain only the GPT, the GPT is {requiredGPTBufferSize} bytes long, the Block size is {BlockSize} bytes long. The overlapping partition is {conflictingPartition.Name}";
                Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                throw new Exception(errorMessage);
            }

            return GPT;
        }

        internal static (FlashPart[], List<Partition> partitions) GetImageSlices(Stream stream, uint BlockSize, string[] excluded, uint sectorSize)
        {
            GPT GPT = GetGPT(stream, BlockSize, sectorSize);
            uint sectorsInABlock = BlockSize / sectorSize;

            Logging.Log($"Sector Size: {sectorSize}");
            Logging.Log($"Block Size: {BlockSize}");
            Logging.Log($"Sectors in a Block: {sectorsInABlock}");

            List<Partition> Partitions = GPT.Partitions;

            bool isUnlocked = GPT.GetPartition(IS_UNLOCKED_PARTITION_NAME) != null;
            bool isUnlockedSpecA = GPT.GetPartition(HACK_PARTITION_NAME) != null && GPT.GetPartition(BACKUP_BS_NV_PARTITION_NAME) != null;

            if (isUnlocked)
            {
                Logging.Log($"The phone is an unlocked Spec B phone, {UEFI_BS_NV_PARTITION_NAME} will be kept in the FFU image for the unlock to work");
            }

            if (isUnlockedSpecA)
            {
                Logging.Log($"The phone is an UEFI unlocked Spec A phone, {UEFI_BS_NV_PARTITION_NAME} will be kept in the FFU image for the unlock to work");
            }

            List<FlashPart> flashParts = [];

            Logging.Log("Partitions with a * appended are ignored partitions");
            Logging.Log("");

            bool previousWasExcluded = true;

            FlashPart? currentFlashPart = null;

            int maxPartitionNameSize = Partitions.Select(x => x.Name.Length).Max() + 1;
            int maxPartitionLastSector = Partitions.Select(x => x.LastSector.ToString().Length).Max() + 1;

            Logging.Log($"{"Name".PadRight(maxPartitionNameSize)} - " +
                $"{"First".PadRight(maxPartitionLastSector)} - " +
                $"{"Last".PadRight(maxPartitionLastSector)} - " +
                $"{"Sectors".PadRight(maxPartitionLastSector)} - " +
                $"{"Blocks".PadRight(maxPartitionLastSector)}",
                Logging.LoggingLevel.Information);
            Logging.Log("");

            foreach (Partition partition in Partitions.OrderBy(x => x.FirstSector))
            {
                bool isExcluded = false;

                if (excluded.Any(x => x == partition.Name))
                {
                    isExcluded = true;
                    if (isUnlocked && partition.Name == UEFI_BS_NV_PARTITION_NAME)
                    {
                        isExcluded = false;
                    }

                    if (isUnlockedSpecA && partition.Name == UEFI_BS_NV_PARTITION_NAME)
                    {
                        isExcluded = false;
                    }
                }

                string name = $"{(isExcluded ? "*" : "")}{partition.Name}";

                Logging.Log($"{name.PadRight(maxPartitionNameSize)} - " +
                    $"{(partition.FirstSector + "s").PadRight(maxPartitionLastSector)} - " +
                    $"{(partition.LastSector + "s").PadRight(maxPartitionLastSector)} - " +
                    $"{(partition.SizeInSectors + "s").PadRight(maxPartitionLastSector)} - " +
                    $"{(partition.SizeInSectors / (double)sectorsInABlock + "c").PadRight(maxPartitionLastSector)}",
                    isExcluded ? Logging.LoggingLevel.Warning : Logging.LoggingLevel.Information);

                if (isExcluded)
                {
                    previousWasExcluded = true;

                    if (currentFlashPart != null)
                    {
                        ulong totalSectors = (ulong)currentFlashPart.Stream.Length / sectorSize;
                        ulong firstSector = currentFlashPart.StartLocation / sectorSize;
                        ulong lastSector = firstSector + totalSectors - 1;

                        if (firstSector % sectorsInABlock != 0)
                        {
                            string errorMessage = $"- The stream doesn't start on a Block boundary (Total Sectors: {totalSectors} - First Sector: {firstSector} - Last Sector: {lastSector}) - Overflow: {firstSector % sectorsInABlock}, a Block is {sectorsInABlock} sectors";
                            Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                            throw new Exception(errorMessage);
                        }

                        if ((lastSector + 1) % sectorsInABlock != 0)
                        {
                            string errorMessage = $"- The stream doesn't end on a Block boundary (Total Sectors: {totalSectors} - First Sector: {firstSector} - Last Sector: {lastSector}) - Overflow: {(lastSector + 1) % sectorsInABlock}, a Block is {sectorsInABlock} sectors";
                            Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                            //throw new Exception(errorMessage);
                            // TODO: Improve here

                            ulong overflowSectors = (lastSector + 1) % sectorsInABlock;
                            ulong sectorsToAddAsPadding = sectorsInABlock - overflowSectors;
                            ulong bytesToAddAsPadding = sectorsToAddAsPadding * sectorSize;

                            ulong newEnding = currentFlashPart.StartLocation + (ulong)currentFlashPart.Stream.Length + bytesToAddAsPadding;
                            long convertedEnding = (long)newEnding;

                            currentFlashPart.Stream = new PartialStream(stream, (long)currentFlashPart.StartLocation, convertedEnding);

                            totalSectors = (ulong)currentFlashPart.Stream.Length / sectorSize;
                            firstSector = currentFlashPart.StartLocation / sectorSize;
                            lastSector = firstSector + totalSectors - 1;

                            if ((lastSector + 1) % sectorsInABlock != 0)
                            {
                                Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                                throw new Exception(errorMessage);
                            }
                        }

                        flashParts.Add(currentFlashPart);
                        currentFlashPart = null;
                    }

                    continue;
                }

                if (previousWasExcluded)
                {
                    currentFlashPart = new FlashPart(stream, partition.FirstSector * sectorSize);
                }

                previousWasExcluded = false;
                currentFlashPart.Stream = new PartialStream(stream, (long)currentFlashPart.StartLocation, (long)(partition.LastSector + 1) * sectorSize);
            }

            if (!previousWasExcluded)
            {
                if (currentFlashPart != null)
                {
                    ulong totalSectors = (ulong)currentFlashPart.Stream.Length / sectorSize;
                    ulong firstSector = currentFlashPart.StartLocation / sectorSize;
                    ulong lastSector = firstSector + totalSectors - 1;

                    if (firstSector % sectorsInABlock != 0)
                    {
                        string errorMessage = $"- The stream doesn't start on a Block boundary (Total Sectors: {totalSectors} - First Sector: {firstSector} - Last Sector: {lastSector}) - Overflow: {firstSector % sectorsInABlock}, a Block is {sectorsInABlock} sectors";
                        Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                        throw new Exception(errorMessage);
                    }

                    if ((lastSector + 1) % sectorsInABlock != 0)
                    {
                        string errorMessage = $"- The stream doesn't end on a Block boundary (Total Sectors: {totalSectors} - First Sector: {firstSector} - Last Sector: {lastSector}) - Overflow: {(lastSector + 1) % sectorsInABlock}, a Block is {sectorsInABlock} sectors";
                        Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                        //throw new Exception(errorMessage);
                        // TODO: Improve here

                        ulong overflowSectors = (lastSector + 1) % sectorsInABlock;
                        ulong sectorsToAddAsPadding = sectorsInABlock - overflowSectors;
                        ulong bytesToAddAsPadding = sectorsToAddAsPadding * sectorSize;

                        ulong newEnding = currentFlashPart.StartLocation + (ulong)currentFlashPart.Stream.Length + bytesToAddAsPadding;
                        long convertedEnding = (long)newEnding;

                        currentFlashPart.Stream = new PartialStream(stream, (long)currentFlashPart.StartLocation, convertedEnding);

                        totalSectors = (ulong)currentFlashPart.Stream.Length / sectorSize;
                        firstSector = currentFlashPart.StartLocation / sectorSize;
                        lastSector = firstSector + totalSectors - 1;

                        if ((lastSector + 1) % sectorsInABlock != 0)
                        {
                            Logging.Log(errorMessage, Logging.LoggingLevel.Error);
                            throw new Exception(errorMessage);
                        }
                    }

                    flashParts.Add(currentFlashPart);
                }
            }

            Logging.Log("");
            Logging.Log("Final Flash Parts");
            Logging.Log("");
            FlashPart[] finalFlashParts = [.. flashParts];
            PrintFlashParts(finalFlashParts, sectorSize, BlockSize);
            Logging.Log("");

            return (finalFlashParts, Partitions);
        }

        internal static void PrintFlashParts(FlashPart[] finalFlashParts, uint sectorSize, uint BlockSize)
        {
            for (int i = 0; i < finalFlashParts.Length; i++)
            {
                FlashPart flashPart = finalFlashParts[i];
                PrintFlashPart(flashPart, sectorSize, BlockSize, $"FlashPart[{i}]");
            }
        }

        internal static void PrintFlashPart(FlashPart flashPart, uint sectorSize, uint BlockSize, string name)
        {
            uint sectorsInABlock = BlockSize / sectorSize;

            ulong totalSectors = (ulong)flashPart.Stream.Length / sectorSize;
            ulong firstSector = flashPart.StartLocation / sectorSize;
            ulong lastSector = firstSector + totalSectors - 1;

            Logging.Log($"{name} - {firstSector}s - {lastSector}s - {totalSectors}s - {totalSectors / (double)sectorsInABlock}c", Logging.LoggingLevel.Information);
        }
    }
}