using System;
using System.IO;

namespace NeuralNetworkRegression
{
  internal class NeuralRegressionProgram
  {
    static void Main(string[] args)
    {
      Console.WriteLine("\nNeural network " +
        "regression C# ");

      Console.WriteLine("Predict income from sex," +
        " age, State, political leaning ");

      // ----------------------------------------------------

      Console.WriteLine("\nLoading train and" +
        " test data from file ");

      string trainFile =
        "..\\..\\..\\Data\\people_train.txt";
      // sex, age,  State,  income,  politics
      //  0   0.32  1 0 0   0.65400  0 0 1
      double[][] trainX = Utils.MatLoad(trainFile,
        new int[] { 0, 1, 2, 3, 4, 6, 7, 8 }, ',', "#");
      double[] trainY =
        Utils.MatToVec(Utils.MatLoad(trainFile,
        new int[] { 5 }, ',', "#"));

      string testFile =
        "..\\..\\..\\Data\\people_test.txt";
      double[][] testX = Utils.MatLoad(testFile,
        new int[] { 0, 1, 2, 3, 4, 6, 7, 8 }, ',', "#");
      double[] testY =
        Utils.MatToVec(Utils.MatLoad(testFile,
        new int[] { 5 }, ',', "#"));
      Console.WriteLine("Done ");

      Console.WriteLine("\nFirst three X data: ");
      for (int i = 0; i < 3; ++i)
        Utils.VecShow(trainX[i], 2, 6, true);

      Console.WriteLine("\nFirst three target Y: ");
      for (int i = 0; i < 3; ++i)
        Console.WriteLine(trainY[i].ToString("F5"));

      // ----------------------------------------------------

      Console.WriteLine("\nCreating 8-100-1 tanh()" +
        " identity() neural network ");
      NeuralNetwork nn =
        new NeuralNetwork(8, 100, 1, seed: 0);
      Console.WriteLine("Done ");

      // batch training params
      Console.WriteLine("\nPreparing train parameters ");
      int maxEpochs = 2000;
      double lrnRate = 0.01;  // if divide grads by batSize
      int batSize = 10;

      Console.WriteLine("\nmaxEpochs = " +
        maxEpochs);
      Console.WriteLine("lrnRate = " +
        lrnRate.ToString("F3"));
      Console.WriteLine("batSize = " + batSize);

      Console.WriteLine("\nStarting (batch) training ");
      nn.TrainBatch(trainX, trainY, lrnRate,
        batSize, maxEpochs);
      Console.WriteLine("Done ");

      Console.WriteLine("\nEvaluating model ");
      double trainAcc = nn.Accuracy(trainX, trainY, 0.10);
      Console.WriteLine("Accuracy on train data = " +
        trainAcc.ToString("F4"));

      double testAcc = nn.Accuracy(testX, testY, 0.10);
      Console.WriteLine("Accuracy on test data  = " +
        testAcc.ToString("F4"));

      Console.WriteLine("\nPredicting income for male" +
        " 34 Oklahoma moderate ");
      double[] X = new double[] { 0, 0.34, 0, 0, 1, 0, 1, 0 };
      double y = nn.ComputeOutput(X);
      Console.WriteLine("Predicted income = " +
        y.ToString("F5"));

      //// save trained model wts
      //Console.WriteLine("\nSaving model wts to file ");
      //string fn = "..\\..\\..\\Models\\people_wts.txt";
      //nn.SaveWeights(fn);
      //Console.WriteLine("Done ");

      //// load saved wts later 
      //Console.WriteLine("\nLoading saved wts to new NN ");
      //NeuralNetwork nn2 = new NeuralNetwork(8, 100, 1, 0);
      //nn2.LoadWeights(fn);
      //Console.WriteLine("Done ");

      //Console.WriteLine("\nPredicting income for male" +
      //  " 34 Oklahoma moderate ");
      //double y2 = nn2.ComputeOutput(X);
      //Console.WriteLine("Predicted income = " +
      //  y2.ToString("F5"));

      Console.WriteLine("\nEnd demo ");
      Console.ReadLine();
    } // Main

  } // Program

  // --------------------------------------------------------

  public class NeuralNetwork
  {
    private int ni; // number input nodes
    private int nh;
    private int no;

    private double[] iNodes;
    private double[][] ihWeights; // input-hidden
    private double[] hBiases;
    private double[] hNodes;

    private double[][] hoWeights; // hidden-output
    private double[] oBiases;
    private double[] oNodes;  // single val as array

    private Random rnd;

    // ------------------------------------------------------

    public NeuralNetwork(int numIn, int numHid,
      int numOut, int seed)
    {
      this.ni = numIn;
      this.nh = numHid;
      this.no = numOut;  // 1 for regression

      this.iNodes = new double[numIn];

      this.ihWeights = Utils.MatCreate(numIn, numHid);
      this.hBiases = new double[numHid];
      this.hNodes = new double[numHid];

      this.hoWeights = Utils.MatCreate(numHid, numOut);
      this.oBiases = new double[numOut];  // [1]
      this.oNodes = new double[numOut];  // [1]

      this.rnd = new Random(seed);
      this.InitWeights(); // all weights and biases
    } // ctor

    // ------------------------------------------------------

    private void InitWeights() // helper for ctor
    {
      // weights and biases to small random values
      double lo = -0.01; double hi = +0.01;
      int numWts = (this.ni * this.nh) +
        (this.nh * this.no) + this.nh + this.no;
      double[] initialWeights = new double[numWts];
      for (int i = 0; i < initialWeights.Length; ++i)
        initialWeights[i] =
          (hi - lo) * rnd.NextDouble() + lo;
      this.SetWeights(initialWeights);
    }

    // ------------------------------------------------------

    public void SetWeights(double[] wts)
    {
      // copy serialized weights and biases in wts[] 
      // to ih weights, ih biases, ho weights, ho biases
      int numWts = (this.ni * this.nh) +
        (this.nh * this.no) + this.nh + this.no;
      if (wts.Length != numWts)
        throw new Exception("Bad array in SetWeights");

      int k = 0; // points into wts param

      for (int i = 0; i < this.ni; ++i)
        for (int j = 0; j < this.nh; ++j)
          this.ihWeights[i][j] = wts[k++];
      for (int i = 0; i < this.nh; ++i)
        this.hBiases[i] = wts[k++];
      for (int i = 0; i < this.nh; ++i)
        for (int j = 0; j < this.no; ++j)
          this.hoWeights[i][j] = wts[k++];
      for (int i = 0; i < this.no; ++i)
        this.oBiases[i] = wts[k++];
    }

    // ------------------------------------------------------

    public double[] GetWeights()
    {
      int numWts = (this.ni * this.nh) +
        (this.nh * this.no) + this.nh + this.no;
      double[] result = new double[numWts];
      int k = 0;
      for (int i = 0; i < ihWeights.Length; ++i)
        for (int j = 0; j < this.ihWeights[0].Length; ++j)
          result[k++] = this.ihWeights[i][j];
      for (int i = 0; i < this.hBiases.Length; ++i)
        result[k++] = this.hBiases[i];
      for (int i = 0; i < this.hoWeights.Length; ++i)
        for (int j = 0; j < this.hoWeights[0].Length; ++j)
          result[k++] = this.hoWeights[i][j];
      for (int i = 0; i < this.oBiases.Length; ++i)
        result[k++] = this.oBiases[i];
      return result;
    }

    // ------------------------------------------------------

    public double ComputeOutput(double[] x)
    {
      double[] hSums = new double[this.nh]; // scratch 
      double[] oSums = new double[this.no]; // out sums

      for (int i = 0; i < x.Length; ++i)
        this.iNodes[i] = x[i];
      // note: no need to copy x-values unless
      // you implement a ToString.
      // more efficient to simply use the X[] directly.

      // 1. compute i-h sum of weights * inputs
      for (int j = 0; j < this.nh; ++j)
        for (int i = 0; i < this.ni; ++i)
          hSums[j] += this.iNodes[i] *
            this.ihWeights[i][j]; // note +=

      // 2. add biases to hidden sums
      for (int i = 0; i < this.nh; ++i)
        hSums[i] += this.hBiases[i];

      // 3. apply hidden activation
      for (int i = 0; i < this.nh; ++i)
        this.hNodes[i] = HyperTan(hSums[i]);

      // 4. compute h-o sum of wts * hOutputs
      for (int j = 0; j < this.no; ++j)
        for (int i = 0; i < this.nh; ++i)
          oSums[j] += this.hNodes[i] *
            this.hoWeights[i][j];  // [1]

      // 5. add biases to output sums
      for (int i = 0; i < this.no; ++i)
        oSums[i] += this.oBiases[i];

      // 6. apply output activation
      for (int i = 0; i < this.no; ++i)
        this.oNodes[i] = Identity(oSums[i]);

      return this.oNodes[0];  // single value
    }

    // ------------------------------------------------------

    private static double HyperTan(double x)
    {
      if (x < -10.0) return -1.0;
      else if (x > 10.0) return 1.0;
      else return Math.Tanh(x);
    }

    // ------------------------------------------------------

    private static double Identity(double x)
    {
      return x;
    }

    // ------------------------------------------------------

    public void TrainBatch(double[][] trainX,
      double[] trainY, double lrnRate, int batSize,
      int maxEpochs)
    {
      // "batch" / "mini-batch" version

      // create accumulated grads
      double[][] hoGrads =
        Utils.MatCreate(this.nh, this.no);
      double[] obGrads = new double[this.no];
      double[][] ihGrads =
        Utils.MatCreate(this.ni, this.nh);
      double[] hbGrads = new double[this.nh];

      double[] oSignals = new double[this.no];
      double[] hSignals = new double[this.nh];

      // create indices
      int n = trainX.Length;
      int[] indices = new int[n];
      for (int i = 0; i < n; ++i)
        indices[i] = i;

      // calc freq of progress and batches-per-epoch
      int freq = maxEpochs / 10;
      int numBatches = n / batSize;  // int division

      for (int epoch = 0; epoch < maxEpochs; ++epoch)
      {
        Shuffle(indices);

        for (int batIdx = 0; batIdx < numBatches; ++batIdx)
        {
          // zero out all grads
          for (int i = 0; i < this.ni; ++i)
          {
            for (int j = 0; j < this.nh; ++j)
            {
              ihGrads[i][j] = 0.0;
            }
          }

          for (int j = 0; j < this.nh; ++j)
          {
            hbGrads[j] = 0.0;
          }

          for (int j = 0; j < this.nh; ++j)
          {
            for (int k = 0; k < this.no; ++k)
            {
              hoGrads[j][k] = 0.0;
            }
          }

          for (int k = 0; k < this.no; ++k)
          {
            obGrads[k] = 0.0;
          }

          // accumulate grads for each item in batch
          //for (int ii = 0; ii < n; ++ii)
          for (int ii = 0; ii < batSize; ++ii)
          {
            int idx = indices[ii];
            double[] x = trainX[idx];
            double y = trainY[idx];
            this.ComputeOutput(x);

            // 1. compute output node scratch signals 
            for (int k = 0; k < this.no; ++k)
            {
              double derivative = 1.0;  // identity
              oSignals[k] = derivative * (this.oNodes[k] - y);
            }

            // ----------------------------------------------

            // 2. accum hidden-to-output gradients 
            for (int j = 0; j < this.nh; ++j)
            {
              for (int k = 0; k < this.no; ++k)
              {
                hoGrads[j][k] += oSignals[k] *
                  this.hNodes[j];  // note the +=
              }
            }

            // 3. accum output node bias gradients
            for (int k = 0; k < this.no; ++k)
            {
              obGrads[k] += oSignals[k] * 1.0;  // 1.0 dummy 
            }

            // ----------------------------------------------

            // 4. compute hidden node signals
            for (int j = 0; j < this.nh; ++j)
            {
              double sum = 0.0;
              for (int k = 0; k < this.no; ++k)
              {
                sum += oSignals[k] * this.hoWeights[j][k];
              }
              double derivative =
                (1 - this.hNodes[j]) *
                (1 + this.hNodes[j]);  // tanh
              hSignals[j] = derivative * sum;
            }

            // ----------------------------------------------

            // 5. accum input-to-hidden gradients
            for (int i = 0; i < this.ni; ++i)
            {
              for (int j = 0; j < this.nh; ++j)
              {
                ihGrads[i][j] += hSignals[j] *
                  this.iNodes[i];
              }
            }

            // ----------------------------------------------

            // 6. accum hidden node bias gradients
            for (int j = 0; j < this.nh; ++j)
            {
              hbGrads[j] += hSignals[j] * 1.0;  // 1.0 dummy
            }

          } // each item in the curr batch

          // divide all accumulated gradients by batch size
          //  a. hidden-to-output gradients 
          for (int j = 0; j < this.nh; ++j)
            for (int k = 0; k < this.no; ++k)
              hoGrads[j][k] /= batSize;

          // b. output node bias gradients
          for (int k = 0; k < this.no; ++k)
            obGrads[k] /= batSize;

          // c. input-to-hidden gradients
          for (int i = 0; i < this.ni; ++i)
            for (int j = 0; j < this.nh; ++j)
              ihGrads[i][j] /= batSize;

          // d. hidden node bias gradients
          for (int j = 0; j < this.nh; ++j)
            hbGrads[j] /= batSize;

          // ------------------------------------------------

          // 7. update input-to-hidden weights
          for (int i = 0; i < this.ni; ++i)
          {
            for (int j = 0; j < this.nh; ++j)
            {
              double delta = -1.0 * lrnRate * ihGrads[i][j];
              this.ihWeights[i][j] += delta;
            }
          }

          // 8. update hidden node biases
          for (int j = 0; j < this.nh; ++j)
          {
            double delta = -1.0 * lrnRate * hbGrads[j];
            this.hBiases[j] += delta;
          }

          // 9. update hidden-to-output weights
          for (int j = 0; j < this.nh; ++j)
          {
            for (int k = 0; k < this.no; ++k)
            {
              double delta = -1.0 * lrnRate * hoGrads[j][k];
              this.hoWeights[j][k] += delta;
            }
          }

          // ------------------------------------------------

          // 10. update output node biases
          for (int k = 0; k < this.no; ++k)
          {
            double delta = -1.0 * lrnRate * obGrads[k];
            this.oBiases[k] += delta;
          }

        } // each batch

        if (epoch % freq == 0)  // progress every few epochs
        {
          double mse = this.Error(trainX, trainY);
          double acc = this.Accuracy(trainX, trainY, 0.10);

          string s1 = "epoch: " + epoch.ToString().PadLeft(4);
          string s2 = "  MSE = " + mse.ToString("F4");
          string s3 = "  acc = " + acc.ToString("F4");
          Console.WriteLine(s1 + s2 + s3);
        }

      } // epoch

    } // TrainBatch

    // ------------------------------------------------------

    private void Shuffle(int[] sequence)
    {
      for (int i = 0; i < sequence.Length; ++i)
      {
        int r = this.rnd.Next(i, sequence.Length);
        int tmp = sequence[r];
        sequence[r] = sequence[i];
        sequence[i] = tmp;
        //sequence[i] = i; // for testing
      }
    } // Shuffle

    // ------------------------------------------------------

    public double Error(double[][] trainX, double[] trainY)
    {
      // MSE
      int n = trainX.Length;
      double sumSquaredError = 0.0;
      for (int i = 0; i < n; ++i)
      {
        double predY = this.ComputeOutput(trainX[i]);
        double actualY = trainY[i];
        sumSquaredError += (predY - actualY) *
          (predY - actualY);
      }
      return sumSquaredError / n;
    } // Error

    // ------------------------------------------------------

    public double Accuracy(double[][] dataX,
      double[] dataY, double pctClose)
    {
      // percentage correct using winner-takes all
      int n = dataX.Length;
      int nCorrect = 0;
      int nWrong = 0;
      for (int i = 0; i < n; ++i)
      {
        double predY = this.ComputeOutput(dataX[i]);
        double actualY = dataY[i];
        if (Math.Abs(predY - actualY) <
          Math.Abs(pctClose * actualY))
          ++nCorrect;
        else
          ++nWrong;
      }
      return (nCorrect * 1.0) / (nCorrect + nWrong);
    }

    // ------------------------------------------------------

    public void SaveWeights(string fn)
    {
      FileStream ofs = new FileStream(fn, FileMode.Create);
      StreamWriter sw = new StreamWriter(ofs);

      double[] wts = this.GetWeights();
      for (int i = 0; i < wts.Length; ++i)
        sw.WriteLine(wts[i].ToString("F8"));  // one per line
      sw.Close();
      ofs.Close();
    }

    public void LoadWeights(string fn)
    {
      FileStream ifs = new FileStream(fn, FileMode.Open);
      StreamReader sr = new StreamReader(ifs);
      List<double> listWts = new List<double>();
      string line = "";  // one wt per line
      while ((line = sr.ReadLine()) != null)
      {
        // if (line.StartsWith(comment) == true)
        //   continue;
        listWts.Add(double.Parse(line));
      }
      sr.Close();
      ifs.Close();

      double[] wts = listWts.ToArray();
      this.SetWeights(wts);
    }

    // ------------------------------------------------------

  } // NeuralNetwork class

  // --------------------------------------------------------

  public class Utils
  {
    public static double[][] VecToMat(double[] vec,
      int rows, int cols)
    {
      // vector to row vec/matrix
      double[][] result = MatCreate(rows, cols);
      int k = 0;
      for (int i = 0; i < rows; ++i)
        for (int j = 0; j < cols; ++j)
          result[i][j] = vec[k++];
      return result;
    }

    // ------------------------------------------------------

    public static double[][] MatCreate(int rows,
      int cols)
    {
      double[][] result = new double[rows][];
      for (int i = 0; i < rows; ++i)
        result[i] = new double[cols];
      return result;
    }

    // ------------------------------------------------------

    static int NumNonCommentLines(string fn,
      string comment)
    {
      int ct = 0;
      string line = "";
      FileStream ifs = new FileStream(fn,
        FileMode.Open);
      StreamReader sr = new StreamReader(ifs);
      while ((line = sr.ReadLine()) != null)
        if (line.StartsWith(comment) == false)
          ++ct;
      sr.Close(); ifs.Close();
      return ct;
    }

    // ------------------------------------------------------

    public static double[][] MatLoad(string fn,
      int[] usecols, char sep, string comment)
    {
      // count number of non-comment lines
      int nRows = NumNonCommentLines(fn, comment);
      int nCols = usecols.Length;
      double[][] result = MatCreate(nRows, nCols);
      string line = "";
      string[] tokens = null;
      FileStream ifs = new FileStream(fn, FileMode.Open);
      StreamReader sr = new StreamReader(ifs);

      int i = 0;
      while ((line = sr.ReadLine()) != null)
      {
        if (line.StartsWith(comment) == true)
          continue;
        tokens = line.Split(sep);
        for (int j = 0; j < nCols; ++j)
        {
          int k = usecols[j];  // into tokens
          result[i][j] = double.Parse(tokens[k]);
        }
        ++i;
      }
      sr.Close(); ifs.Close();
      return result;
    }

    // ------------------------------------------------------

    public static double[] MatToVec(double[][] m)
    {
      int rows = m.Length;
      int cols = m[0].Length;
      double[] result = new double[rows * cols];
      int k = 0;
      for (int i = 0; i < rows; ++i)
        for (int j = 0; j < cols; ++j)
          result[k++] = m[i][j];

      return result;
    }

    // ------------------------------------------------------

    public static void MatShow(double[][] m,
      int dec, int wid)
    {
      for (int i = 0; i < m.Length; ++i)
      {
        for (int j = 0; j < m[0].Length; ++j)
        {
          double v = m[i][j];
          if (Math.Abs(v) < 1.0e-8) v = 0.0; // hack
          Console.Write(v.ToString("F" +
            dec).PadLeft(wid));
        }
        Console.WriteLine("");
      }
    }

    // ------------------------------------------------------

    public static void VecShow(int[] vec, int wid)
    {
      for (int i = 0; i < vec.Length; ++i)
        Console.Write(vec[i].ToString().PadLeft(wid));
      Console.WriteLine("");
    }

    // ------------------------------------------------------

    public static void VecShow(double[] vec,
      int dec, int wid, bool newLine)
    {
      for (int i = 0; i < vec.Length; ++i)
      {
        double x = vec[i];
        if (Math.Abs(x) < 1.0e-8) x = 0.0;
        Console.Write(x.ToString("F" +
          dec).PadLeft(wid));
      }
      if (newLine == true)
        Console.WriteLine("");
    }

  } // Utils class

} // ns