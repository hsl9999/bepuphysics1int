﻿using System;
using BEPUphysics.Collidables;
using BEPUphysics.Collidables.MobileCollidables;
using Microsoft.Xna.Framework;
using BEPUphysics.DataStructures;
using BEPUphysics.MathExtensions;
using BEPUphysics.CollisionShapes.ConvexShapes;
using BEPUphysics.CollisionShapes;
using System.Diagnostics;

namespace BEPUphysics.CollisionTests.Manifolds
{
    ///<summary>
    /// Manages persistent contacts between a convex and an instanced mesh.
    ///</summary>
    public class MobileMeshConvexContactManifold : TriangleMeshConvexContactManifold
    {
        protected MobileMeshCollidable mesh;

        internal RawList<int> overlappedTriangles = new RawList<int>(4);

        ///<summary>
        /// Gets the mesh of the pair.
        ///</summary>
        public MobileMeshCollidable Mesh
        {
            get
            {
                return mesh;
            }
        }

        protected override RigidTransform MeshTransform
        {
            get { return mesh.worldTransform; }
        }

        protected internal override int FindOverlappingTriangles(float dt)
        {
            BoundingBox boundingBox;
            AffineTransform transform = new AffineTransform(mesh.worldTransform.Orientation, mesh.worldTransform.Position);
            convex.Shape.GetLocalBoundingBox(ref convex.worldTransform, ref transform, out boundingBox);
            Vector3 transformedVelocity;
            //Compute the relative velocity with respect to the mesh.  The mesh's bounding tree is NOT expanded with velocity,
            //so whatever motion there is between the two objects needs to be included in the convex's bounding box.
            Vector3.Subtract(ref convex.entity.linearVelocity, ref mesh.entity.linearVelocity, out transformedVelocity);
            //The linear transform is known to be orientation only, so using the transpose is allowed.
            Matrix3X3.TransformTranspose(ref transformedVelocity, ref transform.LinearTransform, out transformedVelocity);
            Vector3.Multiply(ref transformedVelocity, dt, out transformedVelocity);

            if (transformedVelocity.X > 0)
                boundingBox.Max.X += transformedVelocity.X;
            else
                boundingBox.Min.X += transformedVelocity.X;

            if (transformedVelocity.Y > 0)
                boundingBox.Max.Y += transformedVelocity.Y;
            else
                boundingBox.Min.Y += transformedVelocity.Y;

            if (transformedVelocity.Z > 0)
                boundingBox.Max.Z += transformedVelocity.Z;
            else
                boundingBox.Min.Z += transformedVelocity.Z;

            mesh.Shape.TriangleMesh.Tree.GetOverlaps(boundingBox, overlappedTriangles);
            return overlappedTriangles.count;
        }

        protected override void ConfigureTriangle(int i, out TriangleIndices indices)
        {
            MeshBoundingBoxTreeData data = mesh.Shape.TriangleMesh.Data;
            int triangleIndex = overlappedTriangles.Elements[i];
            data.GetTriangle(triangleIndex, out localTriangleShape.vA, out localTriangleShape.vB, out localTriangleShape.vC);
            AffineTransform transform;
            AffineTransform.CreateFromRigidTransform(ref mesh.worldTransform, out transform);
            AffineTransform.Transform(ref localTriangleShape.vA, ref transform, out localTriangleShape.vA);
            AffineTransform.Transform(ref localTriangleShape.vB, ref transform, out localTriangleShape.vB);
            AffineTransform.Transform(ref localTriangleShape.vC, ref transform, out localTriangleShape.vC);
            TriangleSidedness sidedness;
            switch (mesh.Shape.solidity)
            {
                case MobileMeshSolidity.Clockwise:
                    sidedness = TriangleSidedness.Clockwise;
                    break;
                case MobileMeshSolidity.Counterclockwise:
                    sidedness = TriangleSidedness.Counterclockwise;
                    break;
                case MobileMeshSolidity.DoubleSided:
                    sidedness = TriangleSidedness.DoubleSided;
                    break;
                default:
                    sidedness = mesh.Shape.solidSidedness;
                    break;
            }
            localTriangleShape.sidedness = sidedness;
            localTriangleShape.collisionMargin = 0;
            indices = new TriangleIndices();
            indices.A = data.indices[triangleIndex];
            indices.B = data.indices[triangleIndex + 1];
            indices.C = data.indices[triangleIndex + 2];
        }

        protected internal override void CleanUpOverlappingTriangles()
        {
            overlappedTriangles.Clear();
        }

        protected override bool UseImprovedBoundaryHandling
        {
            get { return mesh.improveBoundaryBehavior; }
        }

        enum ContainmentState
        {
            Inside,
            Outside,
            Shell
        }

        ContainmentState previousContainmentState = ContainmentState.Outside;
        Vector3 penetrationAxis = Vector3.Up;
        Vector3 lastValidLocalConvexPosition;
        protected override void ProcessCandidates(RawValueList<ContactData> candidates)
        {
            if (Mesh.Shape.solidity == MobileMeshSolidity.Solid)
            {
                if (candidates.count == 0)
                {

                    //If there's no new contacts on the mesh and it's supposed to be a solid,
                    //then we must check the convex for containment within the shell.
                    //We already know that it's not on the shell, meaning that the shape is either
                    //far enough away outside the shell that there's no contact (and we're done), 
                    //or it's far enough inside the shell that the triangles cannot create contacts.

                    //To find out which it is, raycast against the shell.

                    //So which direction should be used for the raycast?

                    Matrix3X3 orientation;
                    Matrix3X3.CreateFromQuaternion(ref mesh.worldTransform.Orientation, out orientation);

                    Ray ray;
                    Vector3.Subtract(ref convex.worldTransform.Position, ref mesh.worldTransform.Position, out ray.Position);
                    Matrix3X3.TransformTranspose(ref ray.Position, ref orientation, out ray.Position);
                    switch (previousContainmentState)
                    {
                        case ContainmentState.Shell:
                            //Use the existing axis computed by the shell.
                            //The shell only found a world space axis, so we need to transform it now.
                            Matrix3X3.TransformTranspose(ref penetrationAxis, ref orientation, out penetrationAxis);
                            //The penetration axis gotten from a contact faces towards the inside of the mesh, so it needs to be reversed.
                            Vector3.Negate(ref penetrationAxis, out ray.Direction);
                            break;
                        case ContainmentState.Outside:
                            //Use the direction pointing from the current position back to the previous position.
                            Vector3.Subtract(ref lastValidLocalConvexPosition, ref ray.Position, out ray.Direction);
                            //Keep the value around for next frame if we're still inside.
                            ray.Direction.Normalize();
                            penetrationAxis = ray.Direction;
                            break;
                        default:
                        case ContainmentState.Inside:
                            //Continue using the same direction.
                            ray.Direction = penetrationAxis;
                            break;
                    }

                    RayHit hit;
                    if (mesh.Shape.IsRayOriginInMesh(ref ray, out hit))
                    {
                        ContactData newContact = new ContactData();
                        newContact.Id = 2; //Give it a special id so that we know that it came from the inside.
                        Matrix3X3.Transform(ref ray.Position, ref orientation, out newContact.Position);
                        Vector3.Add(ref newContact.Position, ref mesh.worldTransform.Position, out newContact.Position);

                        //Vector3.Negate(ref ray.Direction, out newContact.Normal);
                        newContact.Normal = hit.Normal;
                        newContact.Normal.Normalize();

                        float factor;
                        Vector3.Dot(ref ray.Direction, ref newContact.Normal, out factor);
                        newContact.PenetrationDepth = Math.Abs(factor) * hit.T + convex.Shape.minimumRadius;
                        Matrix3X3.Transform(ref newContact.Normal, ref orientation, out newContact.Normal);
                        //Do not yet create a new contact.  Check to see if an 'inner contact' with id == 2 already exists.
                        bool addContact = true;
                        for (int i = 0; i < contacts.count; i++)
                        {
                            if (contacts.Elements[i].Id == 2)
                            {
                                contacts.Elements[i].Position = newContact.Position;
                                contacts.Elements[i].Normal = newContact.Normal;
                                contacts.Elements[i].PenetrationDepth = newContact.PenetrationDepth;
                                supplementData.Elements[i].BasePenetrationDepth = newContact.PenetrationDepth;
                                supplementData.Elements[i].LocalOffsetA = new Vector3();
                                supplementData.Elements[i].LocalOffsetB = ray.Position; //convex local position in mesh.
                                addContact = false;
                                break;
                            }
                        }
                        if (addContact)
                            Add(ref newContact);

                        previousContainmentState = ContainmentState.Inside;
                    }
                    else
                    {
                        //We're not touching the mesh.
                        lastValidLocalConvexPosition = ray.Position;

                        previousContainmentState = ContainmentState.Outside;
                    }
                }
                else
                {
                    //There exist candidates that interact with the surface, so get rid of any contact created from the inside.
                    for (int i = contacts.count - 1; i >= 0; i--)
                    {
                        if (contacts.Elements[i].Id == 2) //It was created inside!
                        {
                            Remove(i);
                            break;
                        }
                    }

                    int deepest = 0;
                    float maxDepth = -1;
                    for (int i = 0; i < candidates.count; i++)
                    {
                        if (candidates.Elements[i].PenetrationDepth > maxDepth)
                        {
                            deepest = i;
                            maxDepth = candidates.Elements[i].PenetrationDepth;
                        }

                    }
                    //The contact normals are a good estimate of the minimum separating axis.
                    //It's not being put into local space since the transition from shell->inside will take care of that.
                    penetrationAxis = candidates.Elements[deepest].Normal;
                    previousContainmentState = ContainmentState.Shell;

                   // RigidTransform.TransformByInverse(ref convex.entity.position, ref mesh.worldTransform, out lastValidLocalConvexPosition);
                }
            }
        }

        ///<summary>
        /// Cleans up the manifold.
        ///</summary>
        public override void CleanUp()
        {
            mesh = null;
            base.CleanUp();
        }

        ///<summary>
        /// Initializes the manifold.
        ///</summary>
        ///<param name="newCollidableA">First collidable.</param>
        ///<param name="newCollidableB">Second collidable.</param>
        public override void Initialize(Collidable newCollidableA, Collidable newCollidableB)
        {
            convex = newCollidableA as ConvexCollidable;
            mesh = newCollidableB as MobileMeshCollidable;


            if (convex == null || mesh == null)
            {
                convex = newCollidableB as ConvexCollidable;
                mesh = newCollidableA as MobileMeshCollidable;
                if (convex == null || mesh == null)
                    throw new Exception("Inappropriate types used to initialize contact manifold.");
            }

        }


    }
}
