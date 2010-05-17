﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cloo;
using Encog.Neural.Networks.Flat;
using Encog.Neural;
using Encog.Neural.NeuralData;
using Encog.Neural.Data;
using Encog.Neural.Data.Basic;

namespace Encog.Util.CL.Kernels
{
    public class KernelNetworkTrain : EncogKernel
    {

        public float[] Errors { get; set; }
        public float[] Gradients { get; set; }


        private ComputeBuffer<int> paramBuffer;
        private ComputeBuffer<float> weightArrayBuffer;

        private ComputeBuffer<int> layerIndexBuffer;
        private ComputeBuffer<int> layerCountBuffer;
        private ComputeBuffer<int> weightIndexBuffer;

        private ComputeBuffer<float> errorBuffer;
        private ComputeBuffer<float> gradientBuffer;
        private ComputeBuffer<int> activationTypeBuffer;

        private int[] paramArray;
        private int totalOutputLength;
        private float[] weightArray;
        private int layerDeltaSize;
        private int[] activationType;
        private FlatNetwork flat;
        private int trainingLength;
        private ComputeKernel kernel;
        private ComputeCommandQueue commands;
        private int maxUnits;
        private TrainingWorkload[] workload;

        public KernelNetworkTrain(ComputeContext context)
            : base(context, "Encog.Resources.KernelNetTrain.txt")
        {
        }

        public void Train(FlatNetwork flat, INeuralDataSet input, int high, int low)
        {
            if (!(input is IIndexable))
            {
                throw new NeuralNetworkError("Neural network input must support IIndexable");
            }

            IIndexable indexable = (IIndexable)input;
            this.flat = flat;
            this.trainingLength = (high - low) + 1;

            INeuralDataPair pair = BasicNeuralDataPair.CreatePair(flat.InputCount, flat.OutputCount);

            this.workload = new TrainingWorkload[1];
            this.workload[0] = new TrainingWorkload(this.trainingLength, input.InputSize, input.IdealSize);

            int inputIndex = 0;
            int idealIndex = 0;

            for (int i = low; i <= high; i++)
            {
                indexable.GetRecord(i, pair);
                for (int col = 0; col < flat.InputCount; col++)
                {
                    this.workload[0].InputArray[inputIndex++] = (float)pair.Input.Data[col];
                }

                for (int col = 0; col < flat.OutputCount; col++)
                {
                    this.workload[0].IdealArray[idealIndex++] = (float)pair.Ideal.Data[col];
                }
            }



        }

        public void Init()
        {
            this.paramArray = new int[10];
            this.totalOutputLength = flat.LayerOutput.Length * this.trainingLength;
            this.weightArray = new float[flat.Weights.Length];
            this.layerDeltaSize = 0;
            this.activationType = flat.ActivationType;

            for (int i = 0; i < flat.LayerCounts.Length; i++)
            {
                layerDeltaSize += flat.LayerCounts[i];
            }

            /*if( Context.Devices[0].MaxComputeUnits>100000 )
                this.maxUnits = 100000;
            else
                this.maxUnits = (int)Context.Devices[0].MaxComputeUnits;*/

            this.maxUnits = 100;

            this.maxUnits = Math.Min(this.maxUnits, trainingLength);

            paramArray[0] = flat.InputCount;
            paramArray[1] = flat.OutputCount;
            paramArray[2] = flat.LayerCounts.Length;
            paramArray[3] = flat.LayerOutput.Length;
            paramArray[4] = layerDeltaSize;
            paramArray[5] = flat.Weights.Length;
            paramArray[6] = maxUnits-1;// index of last item
            paramArray[7] = Math.Max(this.trainingLength/maxUnits,1);// size each item
            paramArray[8] = Math.Max(this.trainingLength%maxUnits,1);// size of last item


            this.paramBuffer = new ComputeBuffer<int>(Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, paramArray);

            this.errorBuffer = new ComputeBuffer<float>(Context, ComputeMemoryFlags.WriteOnly, this.trainingLength);
            this.gradientBuffer = new ComputeBuffer<float>(Context, ComputeMemoryFlags.WriteOnly, flat.Weights.Length * this.trainingLength);

            this.layerIndexBuffer = new ComputeBuffer<int>(Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, flat.LayerIndex);
            this.layerCountBuffer = new ComputeBuffer<int>(Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, flat.LayerCounts);
            this.weightArrayBuffer = new ComputeBuffer<float>(Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, weightArray);
            this.weightIndexBuffer = new ComputeBuffer<int>(Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, flat.WeightIndex);

            this.activationTypeBuffer = new ComputeBuffer<int>(Context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, activationType);
            this.kernel = Program.CreateKernel("NetworkTrain");
            this.commands = new ComputeCommandQueue(Context, Context.Devices[0], ComputeCommandQueueFlags.None);

            this.workload[0].Init(Context);
        }

        public void Calculate()
        {
            Calculate(0);
        }

        private void Calculate(int index)
        {
            TrainingWorkload workload = this.workload[0];

            for (int i = 0; i < flat.Weights.Length; i++)
                weightArray[i] = (float)flat.Weights[i];

            kernel.SetMemoryArgument(0, paramBuffer);
            kernel.SetMemoryArgument(1, errorBuffer);
            kernel.SetMemoryArgument(2, layerIndexBuffer);
            kernel.SetMemoryArgument(3, layerCountBuffer);
            kernel.SetMemoryArgument(4, weightIndexBuffer);
            kernel.SetMemoryArgument(5, workload.InputBuffer);
            kernel.SetMemoryArgument(6, workload.IdealBuffer);
            kernel.SetMemoryArgument(7, weightArrayBuffer);
            kernel.SetMemoryArgument(8, gradientBuffer);
            kernel.SetMemoryArgument(9, activationTypeBuffer);

            try
            {
                long[] workItems = new long[] { maxUnits };
                ComputeEventList events = new ComputeEventList();
                commands.Write(weightArrayBuffer, true, 0, flat.Weights.Length, weightArray, events);
                commands.Execute(kernel, null, workItems, workItems, events);
                Errors = commands.Read(errorBuffer, true, 0, workload.TrainingLength, events);
                Gradients = commands.Read(gradientBuffer, true, 0, flat.Weights.Length * this.trainingLength, events);
            }
            catch (OutOfResourcesComputeException ex)
            {
                throw new EncogError("GPU is out of resources");
            }
        }
    }
}